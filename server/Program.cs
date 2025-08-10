using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace mcp_lucene_net
{
    public class Program
    {
        private static IndexSearcher? _searcher;
        private static QueryParser? _queryParser;
        private static DirectoryReader? _reader;
        private static FSDirectory? _directory;
        private static Timer? _refreshTimer;
        private static DateTime _lastRefresh = DateTime.MinValue;
        private static readonly object _refreshLock = new object();
        private static readonly LuceneVersion _luceneVersion = LuceneVersion.LUCENE_48;

        public static async Task Main(string[] args)
        {
            var indexPath = args.Length > 0 ? args[0] : "./lucene-index";
            
            if (!InitializeLuceneIndex(indexPath))
            {
                Console.Error.WriteLine($"Failed to initialize Lucene index at: {indexPath}");
                Console.Error.WriteLine("Make sure the index exists and is accessible.");
                return;
            }
            
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.AddConsole(consoleLogOptions =>
            {
                consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();

            await builder.Build().RunAsync();
        }

        private static bool InitializeLuceneIndex(string indexPath)
        {
            try
            {
                _directory = FSDirectory.Open(indexPath);
                
                if (!DirectoryReader.IndexExists(_directory))
                {
                    Console.Error.WriteLine($"No Lucene index found at: {indexPath}");
                    return false;
                }

                _reader = DirectoryReader.Open(_directory);
                _searcher = new IndexSearcher(_reader);
                
                var analyzer = new StandardAnalyzer(_luceneVersion);
                _queryParser = new QueryParser(_luceneVersion, "content", analyzer);
                
                Console.Error.WriteLine($"Successfully loaded Lucene index from: {indexPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error initializing Lucene index: {ex.Message}");
                return false;
            }
        }

        private static void QueueIndexRefresh()
        {
            lock (_refreshLock)
            {
                var now = DateTime.UtcNow;
                var timeSinceLastRefresh = now - _lastRefresh;
                
                // If we refreshed recently (within 30 seconds), don't queue another refresh
                if (timeSinceLastRefresh < TimeSpan.FromSeconds(30))
                {
                    return;
                }
                
                // If no timer is already queued, queue one for 30 seconds from now
                if (_refreshTimer == null)
                {
                    _refreshTimer = new Timer(RefreshIndex, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                }
            }
        }

        private static void RefreshIndex(object? state)
        {
            lock (_refreshLock)
            {
                try
                {
                    if (_reader != null && _directory != null)
                    {
                        var newReader = DirectoryReader.OpenIfChanged(_reader);
                        if (newReader != null)
                        {
                            var oldReader = _reader;
                            _reader = newReader;
                            _searcher = new IndexSearcher(_reader);
                            oldReader.Dispose();
                            Console.Error.WriteLine("Index refreshed with new changes");
                        }
                        _lastRefresh = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error refreshing index: {ex.Message}");
                }
                finally
                {
                    _refreshTimer?.Dispose();
                    _refreshTimer = null;
                }
            }
        }


        public static string SearchIndex(string queryString)
        {
            if (_searcher == null || _queryParser == null)
            {
                return "Error: Index not initialized";
            }

            try
            {
                var query = _queryParser.Parse(queryString);
                var hits = _searcher.Search(query, 10);
                
                var results = new List<object>();
                
                foreach (var scoreDoc in hits.ScoreDocs)
                {
                    var doc = _searcher.Doc(scoreDoc.Doc);
                    results.Add(new
                    {
                        id = doc.Get("id"),
                        title = doc.Get("title"),
                        content = doc.Get("content"),
                        source_document = doc.Get("source_document"),
                        chunk_index = doc.Get("chunk_index") != null ? int.Parse(doc.Get("chunk_index")) : (int?)null,
                        score = scoreDoc.Score
                    });
                }

                // Queue a refresh after this search
                QueueIndexRefresh();

                return JsonSerializer.Serialize(new
                {
                    query = queryString,
                    totalHits = hits.TotalHits,
                    results = results
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                return $"Parse error: {ex.Message}";
            }
        }

        public static string ListIndexedDocuments()
        {
            if (_searcher == null)
            {
                return "Error: Index not initialized";
            }

            try
            {
                var documents = new Dictionary<string, object>();
                var totalDocs = _searcher.IndexReader.NumDocs;
                
                for (int i = 0; i < totalDocs; i++)
                {
                    var doc = _searcher.Doc(i);
                    var sourceDoc = doc.Get("source_document");
                    var title = doc.Get("title");
                    var chunkIndex = doc.Get("chunk_index") != null ? int.Parse(doc.Get("chunk_index")) : 1;
                    
                    if (sourceDoc != null && title != null)
                    {
                        if (!documents.ContainsKey(sourceDoc))
                        {
                            documents[sourceDoc] = new
                            {
                                source_document = sourceDoc,
                                title = title.Contains(" - Part ") ? title.Substring(0, title.LastIndexOf(" - Part ")) : title,
                                chunk_count = 1,
                                max_chunk_index = chunkIndex
                            };
                        }
                        else
                        {
                            var existing = documents[sourceDoc] as dynamic;
                            documents[sourceDoc] = new
                            {
                                source_document = sourceDoc,
                                title = existing?.title,
                                chunk_count = (int)(existing?.chunk_count ?? 0) + 1,
                                max_chunk_index = Math.Max((int)(existing?.max_chunk_index ?? 0), chunkIndex)
                            };
                        }
                    }
                }

                // Queue a refresh after this operation
                QueueIndexRefresh();

                return JsonSerializer.Serialize(new
                {
                    total_documents = documents.Count,
                    total_chunks = totalDocs,
                    documents = documents.Values.OrderBy(d => ((dynamic)d).source_document).ToList()
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                return $"Error listing documents: {ex.Message}";
            }
        }
    }

    [McpServerToolType]
    public static class LuceneTools
    {
        [McpServerTool, Description("Search the Lucene index with a query string")]
        public static string Search(string query)
        {
            return Program.SearchIndex(query);
        }

        [McpServerTool, Description("List all indexed documents with metadata including title and chunk count")]
        public static string ListDocuments()
        {
            return Program.ListIndexedDocuments();
        }
    }
}
