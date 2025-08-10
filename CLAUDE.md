# Claude Development Notes

## Project Architecture

### Server (`/server`)
- MCP server using ModelContextProtocol package
- Provides `Search` tool that accepts query strings
- Returns JSON with document chunks, scores, and metadata
- Uses StandardAnalyzer and searches "content" field by default
- Auto-refreshes index when changes detected

### CLI Tool (`/idx`)
- Document indexer supporting text files and PDFs
- Uses iText for PDF text extraction
- Implements chunking strategy for document fragments

## Document Schema

Each indexed chunk has these fields:
- `id`: "{source_document_id}-chunk-{number:D3}" 
- `title`: Document title with part number for multi-chunk docs
- `content`: ~250 words of text content
- `source_document`: Original document ID (for deletion/updates)
- `chunk_index`: Sequential number within source document

## Chunking Strategy

**Current Settings:**
- Max words: 250
- Overlap: 40 words
- Strategy: Word-based with overlap for context preservation

**Rationale:**
- Optimized for business documents (SRDs, incident reports)
- Balances context preservation with search precision
- Fits well in LLM context windows
- Research-backed settings for technical documentation

## Key Implementation Details

### Update Behavior
Re-indexing same document ID:
1. Deletes all chunks with matching `source_document` field
2. Creates new chunks from updated content
3. Prevents orphaned chunks when document shrinks

### PDF Processing
- Uses iText SimpleTextExtractionStrategy
- Extracts from all pages, combines with line breaks
- Falls back to File.ReadAllText for non-PDF files

### Search Results
Returns up to 10 chunks with:
- Full chunk content (not snippets)
- Lucene relevance scores
- Source document metadata
- Chunk index for potential navigation

## Future Enhancements

### Next/Previous Chunk Navigation
**Potential MCP tools to add:**
- `get_next_chunk(chunk_id)` - Get subsequent chunk in same document
- `get_previous_chunk(chunk_id)` - Get preceding chunk
- `get_document_chunks(source_document_id, start_index, count)` - Get range of chunks

**Implementation approach:**
```csharp
// Get next chunk
var currentChunk = GetChunkById(chunkId);
var query = new BooleanQuery();
query.Add(new TermQuery(new Term("source_document", currentChunk.SourceDocument)), Occur.MUST);
query.Add(NumericRangeQuery.NewInt32Range("chunk_index", currentChunk.ChunkIndex + 1, currentChunk.ChunkIndex + 1, true, true), Occur.MUST);
```

**Decision:** Deferred until user testing shows need. Current 40-word overlap and multi-chunk search results should handle most use cases.

## Testing Notes

### Build Commands
```bash
cd server && dotnet build
cd idx && dotnet build
```

### Sample Index Commands
```bash
cd idx
dotnet run -- init --index-path ../test-index
dotnet run -- add --id "test" --title "Test Doc" --content "This is a test document with enough content to potentially create multiple chunks..."
```

## Dependencies

### Server
- Lucene.NET 4.8.0-beta00017
- Lucene.NET.Analysis.Common
- Lucene.NET.QueryParser  
- ModelContextProtocol 0.3.0-preview.2

### CLI Tool
- Lucene.NET 4.8.0-beta00017
- Lucene.NET.Analysis.Common
- System.CommandLine 2.0.0-beta4.22272.1
- iText 9.2.0