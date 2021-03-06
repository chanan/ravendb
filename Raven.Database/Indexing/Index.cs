//-----------------------------------------------------------------------
// <copyright file="Index.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Threading;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using NLog;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Linq;
using Raven.Abstractions.MEF;
using Raven.Database.Data;
using Raven.Database.Extensions;
using Raven.Database.Linq;
using Raven.Database.Plugins;
using Raven.Database.Storage;
using Raven.Json.Linq;
using Version = Lucene.Net.Util.Version;

namespace Raven.Database.Indexing
{
	/// <summary>
	/// 	This is a thread safe, single instance for a particular index.
	/// </summary>
	public abstract class Index : IDisposable
	{
		protected static readonly Logger logIndexing = LogManager.GetLogger(typeof (Index).FullName + ".Indexing");
		protected static readonly Logger logQuerying = LogManager.GetLogger(typeof(Index).FullName + ".Querying");
		private readonly List<Document> currentlyIndexDocuments = new List<Document>();
		private Directory directory;
		protected readonly IndexDefinition indexDefinition;

		private readonly ConcurrentDictionary<string, IIndexExtension> indexExtensions =
			new ConcurrentDictionary<string, IIndexExtension>();

		protected readonly string name;

		private readonly AbstractViewGenerator viewGenerator;
		private readonly object writeLock = new object();
		private volatile bool disposed;
		private IndexWriter indexWriter;
		private readonly IndexSearcherHolder currentIndexSearcherHolder = new IndexSearcherHolder();


		protected Index(Directory directory, string name, IndexDefinition indexDefinition, AbstractViewGenerator viewGenerator)
		{
			if (directory == null) throw new ArgumentNullException("directory");
			if (name == null) throw new ArgumentNullException("name");
			if (indexDefinition == null) throw new ArgumentNullException("indexDefinition");
			if (viewGenerator == null) throw new ArgumentNullException("viewGenerator");

			this.name = name;
			this.indexDefinition = indexDefinition;
			this.viewGenerator = viewGenerator;
			logIndexing.Debug("Creating index for {0}", name);
			this.directory = directory;

			RecreateSearcher();
		}

		[ImportMany]
		public OrderedPartCollection<AbstractAnalyzerGenerator> AnalyzerGenerators { get; set; }

		/// <summary>
		/// Whatever this is a map reduce index or not
		/// </summary>
		public abstract bool IsMapReduce { get; }

		#region IDisposable Members

		public void Dispose()
		{
			lock (writeLock)
			{
				disposed = true;
				foreach (var indexExtension in indexExtensions)
				{
					indexExtension.Value.Dispose();
				}
				if(currentIndexSearcherHolder != null)
				{
					currentIndexSearcherHolder.SetIndexSearcher(null);
				}
				if (indexWriter != null)
				{
					IndexWriter writer = indexWriter;
					indexWriter = null;
					try
					{
						writer.GetAnalyzer().Close();
						writer.Close();
					}
					catch (Exception e)
					{
						logIndexing.ErrorException("Error when closing the index", e);
					}
				}
				try
				{
					directory.Close();
				}
				catch (Exception e)
				{
					logIndexing.ErrorException("Error when closing the directory", e);
				}
			}
		}

		#endregion

		public void Flush()
		{
			lock (writeLock)
			{
				if (disposed)
					return;
				if (indexWriter != null)
				{
					indexWriter.Commit();
				}
			}
		}

		public abstract void IndexDocuments(AbstractViewGenerator viewGenerator, IEnumerable<object> documents,
		                                    WorkContext context, IStorageActionsAccessor actions, DateTime minimumTimestamp);


		protected virtual IndexQueryResult RetrieveDocument(Document document, FieldsToFetch fieldsToFetch, float score)
		{
			return new IndexQueryResult
			{
				Score = score,
				Key = document.Get(Constants.DocumentIdFieldName),
				Projection = fieldsToFetch.IsProjection ? CreateDocumentFromFields(document, fieldsToFetch) : null
			};
		}

		private static RavenJObject CreateDocumentFromFields(Document document, IEnumerable<string> fieldsToFetch)
		{
			var documentFromFields = new RavenJObject();
			var q = fieldsToFetch
				.SelectMany(name => document.GetFields(name) ?? new Field[0])
				.Where(x => x != null)
				.Where(
					x =>
					x.Name().EndsWith("_IsArray") == false && 
					x.Name().EndsWith("_Range") == false &&
					x.Name().EndsWith("_ConvertToJson") == false)
				.Select(fld => CreateProperty(fld, document))
				.GroupBy(x => x.Key)
				.Select(g =>
				{
					if (g.Count() == 1 && document.GetField(g.Key + "_IsArray") == null)
					{
						return g.First();
					}
					return new KeyValuePair<string, RavenJToken>(g.Key, new RavenJArray(g.Select(x => x.Value)));
				});
			foreach (var keyValuePair in q)
			{
				documentFromFields.Add(keyValuePair.Key, keyValuePair.Value);
			}
			return documentFromFields;
		}

		private static KeyValuePair<string, RavenJToken> CreateProperty(Field fld, Document document)
		{
			var stringValue = fld.StringValue();
			if (document.GetField(fld.Name() + "_ConvertToJson") != null)
			{
				var val = RavenJToken.Parse(fld.StringValue()) as RavenJObject;
				return new KeyValuePair<string, RavenJToken>(fld.Name(), val);
			}
			if (stringValue == Constants.NullValue)
				stringValue = null;
			if (stringValue == Constants.EmptyString)
				stringValue = string.Empty; 
			return new KeyValuePair<string, RavenJToken>(fld.Name(), stringValue);
		}

		protected void Write(WorkContext context, Func<IndexWriter, Analyzer, bool> action)
		{
			if (disposed)
				throw new ObjectDisposedException("Index " + name + " has been disposed");
			lock (writeLock)
			{
				bool shouldRecreateSearcher;
				var toDispose = new List<Action>();
				Analyzer searchAnalyzer = null;
				try
				{
					try
					{
						searchAnalyzer = CreateAnalyzer(new LowerCaseKeywordAnalyzer(), toDispose);
					}
					catch (Exception e)
					{
						context.AddError(name, "Creating Analyzer", e.ToString());
						throw;
					}

					if (indexWriter == null)
					{
						indexWriter = CreateIndexWriter(directory);
					}

					try
					{
						shouldRecreateSearcher = action(indexWriter, searchAnalyzer);
						foreach (IIndexExtension indexExtension in indexExtensions.Values)
						{
							indexExtension.OnDocumentsIndexed(currentlyIndexDocuments);
						}
					}
					catch (Exception e)
					{
						context.AddError(name, null, e.ToString());
						throw;
					}

					WriteTempIndexToDiskIfNeeded(context);
				}
				finally
				{
					currentlyIndexDocuments.Clear();
					if (searchAnalyzer != null)
						searchAnalyzer.Close();
					foreach (Action dispose in toDispose)
					{
						dispose();
					}
				}
				if (shouldRecreateSearcher)
					RecreateSearcher();
			}
		}

		private static IndexWriter CreateIndexWriter(Directory directory)
		{
			var indexWriter = new IndexWriter(directory, new StopAnalyzer(Version.LUCENE_29), IndexWriter.MaxFieldLength.UNLIMITED);
			var mergeScheduler = indexWriter.GetMergeScheduler();
			if(mergeScheduler != null)
				mergeScheduler.Close();
			indexWriter.SetMergeScheduler(new ErrorLoggingConcurrentMergeScheduler());
			return indexWriter;
		}

		private void WriteTempIndexToDiskIfNeeded(WorkContext context)
		{
			if (context.Configuration.RunInMemory || !indexDefinition.IsTemp) 
				return;

			var dir = indexWriter.GetDirectory() as RAMDirectory;
			if (dir == null ||
				dir.SizeInBytes() < context.Configuration.TempIndexInMemoryMaxBytes) 
				return;

			indexWriter.Commit();
			var fsDir = context.IndexStorage.MakeRAMDirectoryPhysical(dir, indexDefinition.Name);
			directory = fsDir;

			indexWriter.GetAnalyzer().Close();
			indexWriter.Close();

			indexWriter = CreateIndexWriter(directory);
		}

		public PerFieldAnalyzerWrapper CreateAnalyzer(Analyzer defaultAnalyzer, ICollection<Action> toDispose)
		{
			toDispose.Add(defaultAnalyzer.Close);
			var perFieldAnalyzerWrapper = new PerFieldAnalyzerWrapper(defaultAnalyzer);
			foreach (var analyzer in indexDefinition.Analyzers)
			{
				Analyzer analyzerInstance = IndexingExtensions.CreateAnalyzerInstance(analyzer.Key, analyzer.Value);
				if (analyzerInstance == null)
					continue;
				toDispose.Add(analyzerInstance.Close);
				perFieldAnalyzerWrapper.AddAnalyzer(analyzer.Key, analyzerInstance);
			}
			StandardAnalyzer standardAnalyzer = null;
			KeywordAnalyzer keywordAnalyzer = null;
			foreach (var fieldIndexing in indexDefinition.Indexes)
			{
				switch (fieldIndexing.Value)
				{
					case FieldIndexing.NotAnalyzed:
						if (keywordAnalyzer == null)
						{
							keywordAnalyzer = new KeywordAnalyzer();
							toDispose.Add(keywordAnalyzer.Close);
						}
						perFieldAnalyzerWrapper.AddAnalyzer(fieldIndexing.Key, keywordAnalyzer);
						break;
					case FieldIndexing.Analyzed:
						if (indexDefinition.Analyzers.ContainsKey(fieldIndexing.Key))
							continue;
						if (standardAnalyzer == null)
						{
							standardAnalyzer = new StandardAnalyzer(Version.LUCENE_29);
							toDispose.Add(standardAnalyzer.Close);
						}
						perFieldAnalyzerWrapper.AddAnalyzer(fieldIndexing.Key, standardAnalyzer);
						break;
				}
			}
			return perFieldAnalyzerWrapper;
		}

		protected IEnumerable<object> RobustEnumerationIndex(IEnumerable<object> input, IEnumerable<IndexingFunc> funcs,
		                                                     IStorageActionsAccessor actions, WorkContext context)
		{
			return new RobustEnumerator(context.Configuration.MaxNumberOfItemsToIndexInSingleBatch)
			{
				BeforeMoveNext = actions.Indexing.IncrementIndexingAttempt,
				CancelMoveNext = actions.Indexing.DecrementIndexingAttempt,
				OnError = (exception, o) =>
				{
					context.AddError(name,
					                 TryGetDocKey(o),
					                 exception.Message
						);
					logIndexing.WarnException(
						String.Format("Failed to execute indexing function on {0} on {1}", name,
					                       TryGetDocKey(o)),
						exception);
					try
					{
						actions.Indexing.IncrementIndexingFailure();
					}
					catch (Exception e)
					{
						// we don't care about error here, because it is an error on error problem
						logIndexing.WarnException(
							String.Format("Could not increment indexing failure rate for {0}", name),
							e);
					}
				}
			}.RobustEnumeration(input, funcs);
		}

		protected IEnumerable<object> RobustEnumerationReduce(IEnumerable<object> input, IndexingFunc func,
		                                                      IStorageActionsAccessor actions, WorkContext context)
		{
			// not strictly accurate, but if we get that many errors, probably an error anyway.
			return new RobustEnumerator(context.Configuration.MaxNumberOfItemsToIndexInSingleBatch)
			{
				BeforeMoveNext = actions.Indexing.IncrementReduceIndexingAttempt,
				CancelMoveNext = actions.Indexing.DecrementReduceIndexingAttempt,
				OnError = (exception, o) =>
				{
					context.AddError(name,
					                 TryGetDocKey(o),
					                 exception.Message
						);
					logIndexing.WarnException(
						String.Format("Failed to execute indexing function on {0} on {1}", name,
					                       TryGetDocKey(o)),
						exception);
					try
					{
						actions.Indexing.IncrementReduceIndexingFailure();
					}
					catch (Exception e)
					{
						// we don't care about error here, because it is an error on error problem
						logIndexing.WarnException(
							String.Format("Could not increment indexing failure rate for {0}", name),
							e);
					}
				}
			}.RobustEnumeration(input, func);
		}

		// we don't care about tracking map/reduce stats here, since it is merely
		// an optimization step
		protected IEnumerable<object> RobustEnumerationReduceDuringMapPhase(IEnumerable<object> input, IndexingFunc func,
															  IStorageActionsAccessor actions, WorkContext context)
		{
			// not strictly accurate, but if we get that many errors, probably an error anyway.
			return new RobustEnumerator(context.Configuration.MaxNumberOfItemsToIndexInSingleBatch)
			{
				BeforeMoveNext = () => { }, // don't care
				CancelMoveNext = () => { }, // don't care
				OnError = (exception, o) =>
				{
					context.AddError(name,
									 TryGetDocKey(o),
									 exception.Message
						);
					logIndexing.WarnException(
						String.Format("Failed to execute indexing function on {0} on {1}", name,
										   TryGetDocKey(o)),
						exception);
				}
			}.RobustEnumeration(input, func);
		}

		public static string TryGetDocKey(object current)
		{
			var dic = current as DynamicJsonObject;
			if (dic == null)
				return null;
			object value = dic.GetValue(Constants.DocumentIdFieldName);
			if (value == null)
				return null;
			return value.ToString();
		}

		public abstract void Remove(string[] keys, WorkContext context);

		internal IDisposable GetSearcher(out IndexSearcher searcher)
		{
			return currentIndexSearcherHolder.GetSearcher(out searcher);
		}

		private void RecreateSearcher()
		{
		    if (indexWriter == null)
		    {
				currentIndexSearcherHolder.SetIndexSearcher(new IndexSearcher(directory, true));
		    }
		    else
		    {
		        var indexReader = indexWriter.GetReader();
				currentIndexSearcherHolder.SetIndexSearcher(new IndexSearcher(indexReader));
		    }
		}

	    protected void AddDocumentToIndex(IndexWriter currentIndexWriter, Document luceneDoc, Analyzer analyzer)
		{
			Analyzer newAnalyzer = AnalyzerGenerators.Aggregate(analyzer,
			                                                    (currentAnalyzer, generator) =>
			                                                    {
			                                                    	Analyzer generateAnalyzer =
			                                                    		generator.Value.GenerateAnalyzerForIndexing(name, luceneDoc,
			                                                    		                                      currentAnalyzer);
			                                                    	if (generateAnalyzer != currentAnalyzer &&
			                                                    	    currentAnalyzer != analyzer)
			                                                    		currentAnalyzer.Close();
			                                                    	return generateAnalyzer;
			                                                    });

			try
			{
				if (indexExtensions.Count > 0)
					currentlyIndexDocuments.Add(CloneDocument(luceneDoc));

				currentIndexWriter.AddDocument(luceneDoc, newAnalyzer);
			}
			finally
			{
				if (newAnalyzer != analyzer)
					newAnalyzer.Close();
			}
		}

		public IIndexExtension GetExtension(string indexExtensionKey)
		{
			IIndexExtension val;
			indexExtensions.TryGetValue(indexExtensionKey, out val);
			return val;
		}

		public void SetExtension(string indexExtensionKey, IIndexExtension extension)
		{
			indexExtensions.TryAdd(indexExtensionKey, extension);
		}

		private static Document CloneDocument(Document luceneDoc)
		{
			var clonedDocument = new Document();
			foreach (AbstractField field in luceneDoc.GetFields())
			{
				var numericField = field as NumericField;
				if (numericField != null)
				{
					var clonedNumericField = new NumericField(numericField.Name(),
															  numericField.IsStored() ? Field.Store.YES : Field.Store.NO,
															  numericField.IsIndexed());
					var numericValue = numericField.GetNumericValue();
					if (numericValue is int)
					{
						clonedNumericField.SetIntValue((int)numericValue);
					}
					if (numericValue is long)
					{
						clonedNumericField.SetLongValue((long)numericValue);
					}
					if (numericValue is double)
					{
						clonedNumericField.SetDoubleValue((double)numericValue);
					}
					if (numericValue is float)
					{
						clonedNumericField.SetFloatValue((float)numericValue);
					}
					clonedDocument.Add(clonedNumericField);
				}
				else
				{
					Field clonedField;
					if (field.IsBinary())
					{
						clonedField = new Field(field.Name(), field.BinaryValue(),
						                        field.IsStored() ? Field.Store.YES : Field.Store.NO);
					}
					else
					{
						clonedField = new Field(field.Name(), field.StringValue(),
										field.IsStored() ? Field.Store.YES : Field.Store.NO,
										field.IsIndexed()? Field.Index.ANALYZED_NO_NORMS : Field.Index.NOT_ANALYZED_NO_NORMS);
					}
					clonedDocument.Add(clonedField);
				}
			}
			return clonedDocument;
		}


		#region Nested type: IndexQueryOperation

		internal class IndexQueryOperation
		{
			private readonly IndexQuery indexQuery;
			private readonly Index parent;
			private readonly Func<IndexQueryResult, bool> shouldIncludeInResults;
			readonly HashSet<RavenJObject> alreadyReturned;
			private readonly FieldsToFetch fieldsToFetch;
			private readonly OrderedPartCollection<AbstractIndexQueryTrigger> indexQueryTriggers;

			public IndexQueryOperation(Index parent, IndexQuery indexQuery, Func<IndexQueryResult, bool> shouldIncludeInResults, FieldsToFetch fieldsToFetch, OrderedPartCollection<AbstractIndexQueryTrigger> indexQueryTriggers)
			{
				this.parent = parent;
				this.indexQuery = indexQuery;
				this.shouldIncludeInResults = shouldIncludeInResults;
				this.fieldsToFetch = fieldsToFetch;
				this.indexQueryTriggers = indexQueryTriggers;

				if (fieldsToFetch.IsDistinctQuery)
					alreadyReturned = new HashSet<RavenJObject>(new RavenJTokenEqualityComparer());
			}

			public IEnumerable<IndexQueryResult> Query()
			{
				using(IndexStorage.EnsureInvariantCulture())
				{
					AssertQueryDoesNotContainFieldsThatAreNotIndexes();
					IndexSearcher indexSearcher;
					using(parent.GetSearcher(out indexSearcher))
					{
						var luceneQuery = GetLuceneQuery();

						foreach (var indexQueryTrigger in indexQueryTriggers)
						{
							luceneQuery = indexQueryTrigger.Value.ProcessQuery(parent.name, luceneQuery, indexQuery);
						}

						int start = indexQuery.Start;
						int pageSize = indexQuery.PageSize;
						int returnedResults = 0;
						int skippedResultsInCurrentLoop = 0;
						do
						{
							if (skippedResultsInCurrentLoop > 0)
							{
								start = start + pageSize;
								// trying to guesstimate how many results we will need to read from the index
								// to get enough unique documents to match the page size
								pageSize = skippedResultsInCurrentLoop * indexQuery.PageSize;
								skippedResultsInCurrentLoop = 0;
							}
							TopDocs search = ExecuteQuery(indexSearcher, luceneQuery, start, pageSize, indexQuery);
							indexQuery.TotalSize.Value = search.TotalHits;

							RecordResultsAlreadySeenForDistinctQuery(indexSearcher, search, start);

							for (int i = start; i < search.TotalHits && (i - start) < pageSize; i++)
							{
								Document document = indexSearcher.Doc(search.ScoreDocs[i].doc);
								IndexQueryResult indexQueryResult = parent.RetrieveDocument(document, fieldsToFetch, search.ScoreDocs[i].score);
								if (ShouldIncludeInResults(indexQueryResult) == false)
								{
									indexQuery.SkippedResults.Value++;
									skippedResultsInCurrentLoop++;
									continue;
								}

								returnedResults++;
								yield return indexQueryResult;
								if (returnedResults == indexQuery.PageSize)
									yield break;
							}
						} while (skippedResultsInCurrentLoop > 0 && returnedResults < indexQuery.PageSize);
					}
				}
			}

			private bool ShouldIncludeInResults(IndexQueryResult indexQueryResult)
			{
				if (shouldIncludeInResults(indexQueryResult) == false)
					return false;
				if (fieldsToFetch.IsDistinctQuery && alreadyReturned.Add(indexQueryResult.Projection) == false)
						return false;
				return true;
			}

			private void RecordResultsAlreadySeenForDistinctQuery(IndexSearcher indexSearcher, TopDocs search, int start)
			{
				if (fieldsToFetch.IsDistinctQuery == false) 
					return;

				// add results that were already there in previous pages
				var min = Math.Min(start, search.TotalHits);
				for (int i = 0; i < min; i++)
				{
					Document document = indexSearcher.Doc(search.ScoreDocs[i].doc);
					var indexQueryResult = parent.RetrieveDocument(document, fieldsToFetch, search.ScoreDocs[i].score);
					alreadyReturned.Add(indexQueryResult.Projection);
				}
			}

			private void AssertQueryDoesNotContainFieldsThatAreNotIndexes()
			{
				HashSet<string> hashSet = SimpleQueryParser.GetFields(indexQuery.Query);
				foreach (string field in hashSet)
				{
					string f = field;
					if (f.EndsWith("_Range"))
					{
						f = f.Substring(0, f.Length - "_Range".Length);
					}
					if (parent.viewGenerator.ContainsField(f) == false)
						throw new ArgumentException("The field '" + f + "' is not indexed, cannot query on fields that are not indexed");
				}

				if (indexQuery.SortedFields == null)
					return;

				foreach (SortedField field in indexQuery.SortedFields)
				{
					string f = field.Field;
					if (f.EndsWith("_Range"))
					{
						f = f.Substring(0, f.Length - "_Range".Length);
					}
					if (parent.viewGenerator.ContainsField(f) == false && f != Constants.DistanceFieldName)
						throw new ArgumentException("The field '" + f + "' is not indexed, cannot sort on fields that are not indexed");
				}
			}

			public Query GetLuceneQuery()
			{
				string query = indexQuery.Query;
				Query luceneQuery;
				if (String.IsNullOrEmpty(query))
				{
					logQuerying.Debug("Issuing query on index {0} for all documents", parent.name);
					luceneQuery = new MatchAllDocsQuery();
				}
				else
				{
					logQuerying.Debug("Issuing query on index {0} for: {1}", parent.name, query);
					var toDispose = new List<Action>();
					PerFieldAnalyzerWrapper searchAnalyzer = null;
					try
					{
						searchAnalyzer = parent.CreateAnalyzer(new LowerCaseKeywordAnalyzer(), toDispose);
						searchAnalyzer = parent.AnalyzerGenerators.Aggregate(searchAnalyzer, (currentAnalyzer, generator) =>
						{
							Analyzer newAnalyzer = generator.GenerateAnalzyerForQuerying(parent.name, indexQuery.Query, currentAnalyzer);
							if (newAnalyzer != currentAnalyzer)
							{
								DisposeAnalyzerAndFriends(toDispose, currentAnalyzer);
							}
							return parent.CreateAnalyzer(newAnalyzer, toDispose);
						});
						luceneQuery = QueryBuilder.BuildQuery(query, searchAnalyzer);
					}
					finally
					{
						DisposeAnalyzerAndFriends(toDispose, searchAnalyzer);
					}
				}
				return luceneQuery;
			}

			private static void DisposeAnalyzerAndFriends(List<Action> toDispose, PerFieldAnalyzerWrapper analyzer)
			{
				if (analyzer != null)
					analyzer.Close();
				foreach (Action dispose in toDispose)
				{
					dispose();
				}
				toDispose.Clear();
			}

			private TopDocs ExecuteQuery(IndexSearcher indexSearcher, Query luceneQuery, int start, int pageSize,
			                             IndexQuery indexQuery)
			{
				Filter filter = indexQuery.GetFilter();
				Sort sort = indexQuery.GetSort(filter, parent.indexDefinition);

				if (pageSize == Int32.MaxValue) // we want all docs
				{
					var gatherAllCollector = new GatherAllCollector();
					indexSearcher.Search(luceneQuery, filter, gatherAllCollector);
					return gatherAllCollector.ToTopDocs();
				}
				// NOTE: We get Start + Pagesize results back so we have something to page on
				if (sort != null)
				{
					return indexSearcher.Search(luceneQuery, filter, pageSize + start, sort);
				}
				return indexSearcher.Search(luceneQuery, filter, pageSize + start);
			}
		}

		#endregion
	}
}