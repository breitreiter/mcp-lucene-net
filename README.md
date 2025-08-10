# MCP Lucene.NET

A Model Context Protocol (MCP) server for full-text search using Lucene.NET with document chunking support.

## Overview

This project consists of two components:
- **`/server`** - MCP server that provides search functionality over a Lucene.NET index
- **`/idx`** - CLI tool for indexing documents (text files, PDFs) into Lucene.NET

## Quick Start

### 1. Index Documents
```bash
cd idx
dotnet run -- init --index-path ./lucene-index
dotnet run -- add --id "manual" --title "User Manual" --file "./manual.pdf"
dotnet run -- add --id "policy" --title "Privacy Policy" --content "Our privacy policy..."
```

### 2. Start MCP Server
```bash
cd server
dotnet run ./lucene-index
```

### 3. Search Documents
The server exposes a `Search` tool that returns JSON with relevant document chunks.

## MCP Configuration

To configure the server in Claude Desktop or other MCP clients, add this to your MCP configuration:

```json
{
  "mcpServers": {
    "lucene-search": {
      "command": "/path/to/mcp-lucene-net/server/bin/Release/net8.0/server",
      "args": [
        "/path/to/your/lucene-index"
      ],
      "env": {}
    }
  }
}
```

Replace `/path/to/mcp-lucene-net/server/bin/Release/net8.0/server` with the actual path to your compiled server binary and `/path/to/your/lucene-index` with the path to your Lucene index directory.

## Document Chunking

Documents are automatically split into ~250 word chunks with 40 word overlap to optimize search relevance and LLM context usage. Each chunk includes:

- `id`: Unique chunk identifier (e.g., "manual-chunk-001")
- `title`: Descriptive title with part number
- `content`: ~250 words of text
- `source_document`: Original document ID
- `chunk_index`: Sequential chunk number

## CLI Commands

```bash
# Initialize new index
idx init --index-path ./lucene-index

# Add single document
idx add --id "doc1" --title "Title" --content "Text content"
idx add --id "doc2" --title "Title" --file "./document.pdf"

# Bulk add from JSON
idx bulk --index-path ./lucene-index --json "./documents.json"
```

### JSON Format
```json
[
  {
    "Id": "doc1",
    "Title": "Document Title",
    "Content": "Document content..."
  }
]
```

## Requirements

- .NET 8.0
- Lucene.NET 4.8.0-beta
- iText for PDF extraction