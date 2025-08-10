using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using System.CommandLine;
using System.Text.Json;

namespace idx
{
    internal class Program
    {
        private static readonly LuceneVersion _luceneVersion = LuceneVersion.LUCENE_48;

        static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("Lucene.NET document indexing CLI tool");

            var indexPathOption = new Option<string>(
                name: "-i",
                description: "Path to the Lucene index directory",
                getDefaultValue: () => "./lucene-index"
            );

            var initCommand = new Command("init", "Initialize a new Lucene index")
            {
                indexPathOption
            };
            initCommand.SetHandler(InitializeIndex, indexPathOption);

            var addCommand = new Command("add", "Add a single document to the index");
            var idOption = new Option<string>("-d", "Document ID") { IsRequired = true };
            var titleOption = new Option<string>("-t", "Document title") { IsRequired = true };
            var contentOption = new Option<string?>("-c", "Document content");
            var fileOption = new Option<FileInfo?>("-f", "Path to file containing document content");

            addCommand.AddOption(indexPathOption);
            addCommand.AddOption(idOption);
            addCommand.AddOption(titleOption);
            addCommand.AddOption(contentOption);
            addCommand.AddOption(fileOption);
            addCommand.SetHandler(AddDocument, indexPathOption, idOption, titleOption, contentOption, fileOption);

            var bulkCommand = new Command("bulk", "Add documents from JSON file");
            var jsonFileOption = new Option<FileInfo>("-j", "Path to JSON file containing documents") { IsRequired = true };
            
            bulkCommand.AddOption(indexPathOption);
            bulkCommand.AddOption(jsonFileOption);
            bulkCommand.SetHandler(BulkAddDocuments, indexPathOption, jsonFileOption);

            rootCommand.AddCommand(initCommand);
            rootCommand.AddCommand(addCommand);
            rootCommand.AddCommand(bulkCommand);

            rootCommand.SetHandler(() =>
            {
                Console.WriteLine("Lucene.NET document indexing CLI tool");
                Console.WriteLine();
                Console.WriteLine("Available commands:");
                Console.WriteLine("  init    Initialize a new Lucene index");
                Console.WriteLine("  add     Add a single document to the index");
                Console.WriteLine("  bulk    Add documents from JSON file");
                Console.WriteLine();
                Console.WriteLine("Use --help with any command for more details.");
            });

            return await rootCommand.InvokeAsync(args);
        }

        private static void InitializeIndex(string indexPath)
        {
            try
            {
                var directory = FSDirectory.Open(indexPath);
                var analyzer = new StandardAnalyzer(_luceneVersion);
                var config = new IndexWriterConfig(_luceneVersion, analyzer);
                
                using var writer = new IndexWriter(directory, config);
                writer.Commit();
                
                Console.WriteLine($"Successfully initialized Lucene index at: {indexPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error initializing index: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private static void AddDocument(string indexPath, string id, string title, string? content, FileInfo? file)
        {
            if (content == null && file == null)
            {
                Console.Error.WriteLine("Error: Either -c or -f must be provided");
                Environment.Exit(1);
            }

            if (content != null && file != null)
            {
                Console.Error.WriteLine("Error: Cannot specify both -c and -f");
                Environment.Exit(1);
            }

            try
            {
                var documentContent = content;
                if (file != null)
                {
                    if (!file.Exists)
                    {
                        Console.Error.WriteLine($"Error: File not found: {file.FullName}");
                        Environment.Exit(1);
                    }
                    documentContent = ExtractTextFromFile(file);
                }

                var directory = FSDirectory.Open(indexPath);
                var analyzer = new StandardAnalyzer(_luceneVersion);
                var config = new IndexWriterConfig(_luceneVersion, analyzer);

                using var writer = new IndexWriter(directory, config);
                
                // Delete any existing chunks for this document first
                writer.DeleteDocuments(new Term("source_document", id));
                
                // Create chunks from the document
                var chunks = CreateChunks(id, title, documentContent ?? "");
                
                if (chunks.Count == 0)
                {
                    Console.WriteLine($"Warning: Document '{id}' produced no chunks (empty content)");
                    return;
                }
                
                // Index each chunk as a separate document
                foreach (var chunk in chunks)
                {
                    var doc = new Document();
                    doc.Add(new StringField("id", chunk.Id, Field.Store.YES));
                    doc.Add(new TextField("title", chunk.Title, Field.Store.YES));
                    doc.Add(new TextField("content", chunk.Content, Field.Store.YES));
                    doc.Add(new StringField("source_document", chunk.SourceDocument, Field.Store.YES));
                    doc.Add(new Int32Field("chunk_index", chunk.ChunkIndex, Field.Store.YES));
                    
                    writer.AddDocument(doc);
                }
                
                writer.Commit();

                Console.WriteLine($"Successfully added document '{id}' as {chunks.Count} chunks to index");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error adding document: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private static void BulkAddDocuments(string indexPath, FileInfo jsonFile)
        {
            if (!jsonFile.Exists)
            {
                Console.Error.WriteLine($"Error: JSON file not found: {jsonFile.FullName}");
                Environment.Exit(1);
            }

            try
            {
                var jsonContent = File.ReadAllText(jsonFile.FullName);
                var documents = JsonSerializer.Deserialize<DocumentData[]>(jsonContent);
                
                if (documents == null)
                {
                    Console.Error.WriteLine("Error: Invalid JSON format");
                    Environment.Exit(1);
                }

                var directory = FSDirectory.Open(indexPath);
                var analyzer = new StandardAnalyzer(_luceneVersion);
                var config = new IndexWriterConfig(_luceneVersion, analyzer);

                using var writer = new IndexWriter(directory, config);
                
                var addedDocuments = 0;
                var totalChunks = 0;
                
                foreach (var docData in documents)
                {
                    if (string.IsNullOrEmpty(docData.Id) || string.IsNullOrEmpty(docData.Title))
                    {
                        Console.WriteLine($"Skipping document with missing id or title");
                        continue;
                    }

                    // Delete any existing chunks for this document first
                    writer.DeleteDocuments(new Term("source_document", docData.Id));
                    
                    // Create chunks from the document
                    var chunks = CreateChunks(docData.Id, docData.Title, docData.Content ?? "");
                    
                    if (chunks.Count == 0)
                    {
                        Console.WriteLine($"Warning: Document '{docData.Id}' produced no chunks (empty content)");
                        continue;
                    }
                    
                    // Index each chunk as a separate document
                    foreach (var chunk in chunks)
                    {
                        var doc = new Document();
                        doc.Add(new StringField("id", chunk.Id, Field.Store.YES));
                        doc.Add(new TextField("title", chunk.Title, Field.Store.YES));
                        doc.Add(new TextField("content", chunk.Content, Field.Store.YES));
                        doc.Add(new StringField("source_document", chunk.SourceDocument, Field.Store.YES));
                        doc.Add(new Int32Field("chunk_index", chunk.ChunkIndex, Field.Store.YES));
                        
                        writer.AddDocument(doc);
                    }
                    
                    addedDocuments++;
                    totalChunks += chunks.Count;
                }
                
                writer.Commit();
                Console.WriteLine($"Successfully added {addedDocuments} documents as {totalChunks} total chunks to index");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing bulk documents: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private static List<DocumentChunk> CreateChunks(string sourceId, string title, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new List<DocumentChunk>();
            
            var chunks = new List<DocumentChunk>();
            var textChunks = SplitIntoChunks(content, maxWords: 250, overlapWords: 40);
            
            for (int i = 0; i < textChunks.Count; i++)
            {
                var chunkId = $"{sourceId}-chunk-{(i + 1):D3}";
                var chunkTitle = textChunks.Count == 1 ? title : $"{title} - Part {i + 1}";
                
                chunks.Add(new DocumentChunk 
                { 
                    Id = chunkId,
                    Title = chunkTitle,
                    Content = textChunks[i],
                    SourceDocument = sourceId,
                    ChunkIndex = i + 1
                });
            }
            
            return chunks;
        }
        
        private static List<string> SplitIntoChunks(string text, int maxWords, int overlapWords)
        {
            var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= maxWords)
            {
                return new List<string> { text };
            }
            
            var chunks = new List<string>();
            var currentIndex = 0;
            
            while (currentIndex < words.Length)
            {
                var chunkWords = new List<string>();
                var wordsToTake = Math.Min(maxWords, words.Length - currentIndex);
                
                for (int i = 0; i < wordsToTake; i++)
                {
                    chunkWords.Add(words[currentIndex + i]);
                }
                
                chunks.Add(string.Join(" ", chunkWords));
                
                // Move forward by (maxWords - overlapWords) to create overlap
                var nextIndex = currentIndex + maxWords - overlapWords;
                
                // If we're near the end, just move to the end to avoid tiny final chunk
                if (nextIndex >= words.Length - overlapWords)
                {
                    break;
                }
                
                currentIndex = nextIndex;
            }
            
            return chunks;
        }

        private static string ExtractTextFromFile(FileInfo file)
        {
            var extension = file.Extension.ToLowerInvariant();
            
            return extension switch
            {
                ".pdf" => ExtractTextFromPdf(file.FullName),
                ".txt" => File.ReadAllText(file.FullName),
                _ => File.ReadAllText(file.FullName) // Default to treating as text file
            };
        }

        private static string ExtractTextFromPdf(string pdfPath)
        {
            try
            {
                using var pdfReader = new PdfReader(pdfPath);
                using var pdfDocument = new PdfDocument(pdfReader);
                
                var text = new System.Text.StringBuilder();
                var strategy = new SimpleTextExtractionStrategy();
                
                for (int pageNum = 1; pageNum <= pdfDocument.GetNumberOfPages(); pageNum++)
                {
                    var page = pdfDocument.GetPage(pageNum);
                    var pageText = PdfTextExtractor.GetTextFromPage(page, strategy);
                    text.AppendLine(pageText);
                }
                
                return text.ToString();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to extract text from PDF '{pdfPath}': {ex.Message}", ex);
            }
        }
    }

    public class DocumentData
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
    }

    public class DocumentChunk
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string SourceDocument { get; set; } = string.Empty;
        public int ChunkIndex { get; set; }
    }
}
