﻿using Microsoft.Azure.Search;
using Nethereum.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Index = Microsoft.Azure.Search.Models.Index;

namespace Nethereum.BlockchainStore.Search.Azure
{
    public class AzureSearchService : IAzureSearchService
    {
        private readonly SearchServiceClient _client;
        private readonly ConcurrentDictionary<string, Index> _azureIndexes;

        public AzureSearchService(string serviceName, string searchApiKey)
        {
            _client = new SearchServiceClient(serviceName, new SearchCredentials(searchApiKey));
            _azureIndexes = new ConcurrentDictionary<string, Index>();
        }

        public async Task<IEventIndexer<TEvent>> CreateEventIndexer<TEvent>(string indexName = null, bool addPresetEventLogFields = true) where TEvent : class
        {
            return await CreateEventIndexer(new EventIndexDefinition<TEvent>(indexName, addPresetEventLogFields));
        }

        public async Task<IEventIndexer<TEvent>> CreateEventIndexer<TEvent>(EventIndexDefinition<TEvent> searchIndexDefinition) where TEvent : class
        {
            var azureIndex = await GetOrCreateAzureIndex(searchIndexDefinition);
            return new AzureEventIndexer<TEvent>(searchIndexDefinition, azureIndex, GetOrCreateIndexClient(azureIndex.Name));
        }

        public async Task<IEventIndexer<TEvent>> CreateEventIndexer<TEvent, TSearchDocument>(Index index,
            IEventToSearchDocumentMapper<TEvent, TSearchDocument> mapper)
            where TEvent : class
            where TSearchDocument : class, new()
        {
            index = await GetOrCreateAzureIndex(index);
            IEventIndexer<TEvent> indexer = new AzureEventIndexer<TEvent, TSearchDocument>(index, GetOrCreateIndexClient(index.Name), mapper);
            return indexer;
        }

        public async Task<IEventIndexer<TEvent>> CreateEventIndexer<TEvent, TSearchDocument>(Index index, Func<EventLog<TEvent>, TSearchDocument> mappingFunc)
            where TEvent : class
            where TSearchDocument : class, new()
        {
            index = await GetOrCreateAzureIndex(index);
            var mapper = new EventToSearchDocumentMapper<TEvent, TSearchDocument>(mappingFunc);
            IEventIndexer<TEvent> indexer = new AzureEventIndexer<TEvent, TSearchDocument>(index, GetOrCreateIndexClient(index.Name), mapper);
            return indexer;
        }

        public async Task<IFunctionIndexer<TFunctionMessage>> CreateFunctionIndexer<TFunctionMessage>(string indexName = null,bool addPresetEventLogFields = true) 
            where TFunctionMessage : FunctionMessage, new()
        {
            return await CreateFunctionIndexer(new FunctionIndexDefinition<TFunctionMessage>(indexName, addPresetEventLogFields));
        }

        public async Task<IFunctionIndexer<TFunctionMessage>> CreateFunctionIndexer<TFunctionMessage>(
            FunctionIndexDefinition<TFunctionMessage> searchIndexDefinition) 
            where TFunctionMessage : FunctionMessage, new()
        {
            var azureIndex = await GetOrCreateAzureIndex(searchIndexDefinition);
            return new AzureFunctionIndexer<TFunctionMessage>(searchIndexDefinition, azureIndex, GetOrCreateIndexClient(azureIndex.Name));
        }

        public async Task<IFunctionIndexer<TFunctionMessage>> CreateFunctionIndexer<TFunctionMessage, TSearchDocument>(Index index, IFunctionMessageToSearchDocumentMapper<TFunctionMessage, TSearchDocument> mapper)
            where TFunctionMessage : FunctionMessage, new()
            where TSearchDocument : class, new()
        {
            index = await GetOrCreateAzureIndex(index);
            return new AzureFunctionIndexer<TFunctionMessage, TSearchDocument>(index, GetOrCreateIndexClient(index.Name),  mapper);
        }

        public async Task<IFunctionIndexer<TFunctionMessage>> CreateFunctionIndexer<TFunctionMessage, TSearchDocument>(Index index, Func<FunctionCall<TFunctionMessage>, TSearchDocument> mapperFunc)
            where TFunctionMessage : FunctionMessage, new()
            where TSearchDocument : class, new()
        {
            index = await GetOrCreateAzureIndex(index);
            var mapper = new FunctionMessageToSearchDocumentMapper<TFunctionMessage, TSearchDocument>(mapperFunc);
            return new AzureFunctionIndexer<TFunctionMessage, TSearchDocument>(index, GetOrCreateIndexClient(index.Name),  mapper);
        }

        public Task DeleteIndexAsync(IndexDefinition searchIndex) =>
            DeleteIndexAsync(GetAzureIndex(searchIndex).Name);

        public async Task DeleteIndexAsync(string indexName)
        {
            if (await _client.Indexes.ExistsAsync(indexName))
            {
                await _client.Indexes.DeleteAsync(indexName);
            }
        }

        public void Dispose()
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            ((IDisposable)_client)?.Dispose();
        }

        private Index GetAzureIndex(IndexDefinition eventIndex)
        {
            if (_azureIndexes.TryGetValue(eventIndex.IndexName, out var index))
            {
                return index;
            }

            index = eventIndex.ToAzureIndex();
            _azureIndexes.TryAdd(eventIndex.IndexName, index);
            return index;
        }

        protected virtual async Task<Index> GetOrCreateAzureIndex(IndexDefinition indexDefinition)
        {
            var azureIndex = GetAzureIndex(indexDefinition);

            return await GetOrCreateAzureIndex(azureIndex);
        }

        protected virtual async Task<Index> GetOrCreateAzureIndex(Index index)
        {
            if (!await _client.Indexes.ExistsAsync(index.Name))
            {
                index = await _client.Indexes.CreateAsync(index);
            }

            return index;
        }

        public async Task<long> CountDocumentsAsync(string indexName)
        {
            using(var client = _client.Indexes.GetClient(indexName))
            {
                return await client.Documents.CountAsync();
            }
        }

        readonly Dictionary<string, ISearchIndexClient> _clients = new Dictionary<string, ISearchIndexClient>();

        private ISearchIndexClient GetOrCreateIndexClient(string indexName)
        {
            if (_clients.ContainsKey(indexName))
            {
                return _clients[indexName];
            }

            var client = _client.Indexes.GetClient(indexName);
            _clients.Add(indexName, client);
            return client;
        }
    }
}
