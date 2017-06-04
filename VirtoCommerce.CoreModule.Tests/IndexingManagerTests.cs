﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.CoreModule.Data.Indexing;
using VirtoCommerce.Domain.Search;
using Xunit;

namespace VirtoCommerce.CoreModule.Tests
{
    [CLSCompliant(false)]
    [Trait("Category", "CI")]
    public class IndexingManagerTests
    {
        public const string Rebuild = "rebuild";
        public const string Update = "update";
        public const string Primary = "primary";
        public const string Secondary = "secondary";
        public const string DocumentType = "item";

        [Theory]
        [InlineData(Rebuild, 1, Primary)]
        [InlineData(Rebuild, 3, Primary)]
        [InlineData(Update, 1, Primary)]
        [InlineData(Update, 3, Primary)]
        [InlineData(Rebuild, 1, Primary, Secondary)]
        [InlineData(Rebuild, 3, Primary, Secondary)]
        [InlineData(Update, 1, Primary, Secondary)]
        [InlineData(Update, 3, Primary, Secondary)]
        public async Task CanIndexAllDocuments(string operation, int batchSize, params string[] sourceNames)
        {
            var rebuild = operation == Rebuild;

            var searchProvider = new SearchProvider();
            var documentSources = GetDocumentSources(sourceNames);
            var manager = GetIndexingManager(searchProvider, documentSources);
            var progress = new List<IndexingProgress>();
            var cancellationTokenSource = new CancellationTokenSource();

            var options = new IndexingOptions
            {
                DocumentType = DocumentType,
                DeleteExistingIndex = rebuild,
                StartDate = rebuild ? null : (DateTime?)new DateTime(1, 1, 1),
                EndDate = rebuild ? null : (DateTime?)new DateTime(1, 1, 9),
                BatchSize = batchSize,
            };

            await manager.IndexAsync(options, p => progress.Add(p), cancellationTokenSource.Token);

            var expectedBatchesCount = GetExpectedBatchesCount(rebuild, documentSources, batchSize);
            var expectedProgressItemsCount = 1 + (rebuild ? 1 : 0) + expectedBatchesCount * 2 + 1;

            Assert.Equal(expectedProgressItemsCount, progress.Count);

            var i = 0;

            if (rebuild)
            {
                Assert.Equal("Deleting index", progress[i++].Description);
            }

            Assert.Equal("Calculating total count", progress[i++].Description);

            for (var batch = 0; batch < expectedBatchesCount; batch++)
            {
                Assert.Equal("Processing", progress[i++].Description);
                Assert.Equal("Processed", progress[i++].Description);
            }

            Assert.Equal("Completed", progress[i].Description);

            ValidateErrors(progress, "bad1");
            ValidateIndexedDocuments(searchProvider.IndexedDocuments.Values, sourceNames, "good2", "good3");
        }
        [Theory]
        [InlineData(1, Primary)]
        [InlineData(3, Primary)]
        [InlineData(1, Primary, Secondary)]
        [InlineData(3, Primary, Secondary)]
        public async Task CanIndexSpecificDocuments(int batchSize, params string[] sourceNames)
        {
            var searchProvider = new SearchProvider();
            var documentSources = GetDocumentSources(sourceNames);
            var manager = GetIndexingManager(searchProvider, documentSources);
            var progress = new List<IndexingProgress>();
            var cancellationTokenSource = new CancellationTokenSource();

            var options = new IndexingOptions
            {
                DocumentType = DocumentType,
                DocumentIds = new[] { "bad1", "good3", "non-existent-id" },
                BatchSize = batchSize,
            };

            await manager.IndexAsync(options, p => progress.Add(p), cancellationTokenSource.Token);

            var expectedBatchesCount = GetBatchesCount(options.DocumentIds.Count, batchSize);
            var expectedProgressItemsCount = 1 + expectedBatchesCount * 2 + 1;

            Assert.Equal(expectedProgressItemsCount, progress.Count);

            var i = 0;

            Assert.Equal("Calculating total count", progress[i++].Description);

            for (var batch = 0; batch < expectedBatchesCount; batch++)
            {
                Assert.Equal("Processing", progress[i++].Description);
                Assert.Equal("Processed", progress[i++].Description);
            }

            Assert.Equal("Completed", progress[i].Description);

            ValidateErrors(progress, "bad1");
            ValidateIndexedDocuments(searchProvider.IndexedDocuments.Values, sourceNames, "good3");
        }


        private static IList<DocumentSourceBase> GetDocumentSources(IEnumerable<string> names)
        {
            return names.Select(GetDocumentSource).ToArray();
        }

        private static DocumentSourceBase GetDocumentSource(string name)
        {
            switch (name)
            {
                case Primary:
                    return new PrimaryDocumentSource();
                case Secondary:
                    return new SecondaryDocumentSource();
            }

            return null;
        }

        private static int GetExpectedBatchesCount(bool rebuild, IEnumerable<DocumentSourceBase> documentSources, int batchSize)
        {
            int result;

            if (rebuild)
            {
                // Use documents count from primary source
                result = GetBatchesCount(documentSources?.FirstOrDefault()?.Documents.Count ?? 0, batchSize);
            }
            else
            {
                // Calculate batches count for each source and return the maximum value
                result = documentSources?.Max(s => GetBatchesCount(s?.Changes.Count ?? 0, batchSize)) ?? 0;
            }

            return result;
        }

        private static int GetBatchesCount(int itemsCount, int batchSize)
        {
            return (int)Math.Ceiling((decimal)itemsCount / batchSize);
        }

        private static void ValidateErrors(IEnumerable<IndexingProgress> progress, params string[] expectdErrorDoucmentIds)
        {
            var errors = progress
                .Where(p => p.Errors != null)
                .SelectMany(p => p.Errors)
                .ToList();

            Assert.Equal(expectdErrorDoucmentIds.Length, errors.Count);

            foreach (var doucmentId in expectdErrorDoucmentIds)
            {
                Assert.Equal($"ID: {doucmentId}, Error: Search provider error", errors[0]);
            }
        }


        private static void ValidateIndexedDocuments(ICollection<IndexDocument> documents, ICollection<string> expectedFieldNames, params string[] expectedDocumentIds)
        {
            Assert.Equal(expectedDocumentIds.Length, documents.Count);

            foreach (var document in documents)
            {
                Assert.NotNull(document);
                Assert.True(expectedDocumentIds.Contains(document.Id));
                Assert.NotNull(document.Fields);
                Assert.Equal(expectedFieldNames.Count, document.Fields.Count);

                foreach (var fieldName in expectedFieldNames)
                {
                    var field = document.Fields.FirstOrDefault(f => f.Name == fieldName);

                    Assert.NotNull(field);
                    Assert.Equal(fieldName, field.Name);
                    Assert.Equal(document.Id, field.Value);
                }
            }
        }


        private static IIndexingManager GetIndexingManager(ISearchProvider searchProvider, IList<DocumentSourceBase> documentSources)
        {
            var primaryDocumentSource = documentSources?.FirstOrDefault();

            var configuration = new IndexDocumentConfiguration
            {
                DocumentType = DocumentType,
                DocumentSource = CreateIndexDocumentSource(primaryDocumentSource),
                RelatedSources = documentSources?.Skip(1).Select(CreateIndexDocumentSource).ToArray(),
            };

            return new IndexingManager(searchProvider, new[] { configuration });
        }

        private static IndexDocumentSource CreateIndexDocumentSource(DocumentSourceBase documentSource)
        {
            return new IndexDocumentSource
            {
                ChangesProvider = documentSource,
                DocumentBuilder = documentSource,
            };
        }
    }
}
