﻿using Nethereum.BlockchainProcessing.BlockchainProxy;
using Nethereum.BlockchainProcessing.Handlers;
using Nethereum.BlockchainProcessing.Processing;
using Nethereum.BlockchainProcessing.Processing.Logs;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nethereum.BlockchainStore.Search
{
    public class EventIndexingProcessor : IDisposable
    {
        protected readonly List<ILogProcessor> LogProcessors;
        protected readonly List<IIndexer> _indexers;
        protected readonly Func<ulong, ulong?, IBlockProgressService> BlockProgressServiceCallBack;
        protected readonly IEnumerable<NewFilterInput> Filters;
        protected readonly IEventFunctionProcessor FunctionProcessor;

        public EventIndexingProcessor(
            IBlockchainProxyService blockchainProxyService, 
            ISearchService searchService, 
            IEventFunctionProcessor functionProcessor,
            Func<ulong, ulong?, IBlockProgressService> blockProgressServiceCallBack = null, 
            uint maxBlocksPerBatch = 2,
            IEnumerable<NewFilterInput> filters = null,
            uint minimumBlockConfirmations = 0)
        {
            SearchService = searchService;
            BlockchainProxyService = blockchainProxyService;
            MaxBlocksPerBatch = maxBlocksPerBatch;
            Filters = filters;
            MinimumBlockConfirmations = minimumBlockConfirmations;
            BlockProgressServiceCallBack = blockProgressServiceCallBack;
            LogProcessors = new List<ILogProcessor>();
            _indexers = new List<IIndexer>();
            FunctionProcessor = functionProcessor ?? new EventFunctionProcessor(BlockchainProxyService);
        }

        public ISearchService SearchService {get;}
        public IBlockchainProxyService BlockchainProxyService { get; }
        public uint MaxBlocksPerBatch { get; }
        public uint MinimumBlockConfirmations { get; }

        public IReadOnlyList<IIndexer> Indexers => _indexers.AsReadOnly();
        
        public virtual async Task<FunctionIndexTransactionHandler<TFunctionMessage>> CreateFunctionHandlerAsync<TFunctionMessage>(
            string indexName = null)
            where TFunctionMessage : FunctionMessage, new()
        {
            var functionIndexer = await SearchService.CreateFunctionIndexer<TFunctionMessage>(indexName);
            var functionHandler = new FunctionIndexTransactionHandler<TFunctionMessage>(functionIndexer);
            return functionHandler;
        }

        public virtual async Task<IEventIndexProcessor<TEvent>> AddAsync<TEvent>(
            string indexName = null,
            IEnumerable<ITransactionHandler> functionHandlers = null) where TEvent : class, new()
        {

            var indexer = await SearchService.CreateEventIndexer<TEvent>(indexName);
            _indexers.Add(indexer);

            return CreateProcessor(functionHandlers, indexer);
        }

        public virtual async Task<ulong> ProcessAsync(ulong from, ulong? to = null, CancellationTokenSource ctx = null, Action<uint, BlockRange> rangeProcessedCallback = null)
        {
            if(!LogProcessors.Any()) throw new InvalidOperationException("No events to capture - use AddEventAsync to add listeners for indexable events");

            var logProcessor = new BlockchainLogProcessor(
                BlockchainProxyService,
                LogProcessors,
                Filters);

            IBlockProgressService progressService = CreateProgressService(from, to);

            var batchProcessorService = new BlockchainBatchProcessorService(
                logProcessor, progressService, maxNumberOfBlocksPerBatch: MaxBlocksPerBatch);

            if (to != null)
            {
                return await ProcessRange(ctx, rangeProcessedCallback, batchProcessorService);
            }

            return await batchProcessorService.ProcessContinuallyAsync(ctx?.Token ?? new CancellationToken(), rangeProcessedCallback);
            
        }

        private static async Task<ulong> ProcessRange(CancellationTokenSource ctx, Action<uint, BlockRange> rangeProcessedCallback, BlockchainBatchProcessorService batchProcessorService)
        {
            uint blockRangesProcessed = 0;
            ulong blocksProcessed = 0;

            BlockRange? lastBlockRangeProcessed;
            do
            {
                lastBlockRangeProcessed = await batchProcessorService.ProcessLatestBlocksAsync(ctx?.Token ?? new CancellationToken());

                if (lastBlockRangeProcessed != null)
                {
                    blockRangesProcessed++;
                    blocksProcessed += lastBlockRangeProcessed.Value.BlockCount;
                    rangeProcessedCallback?.Invoke(blockRangesProcessed, lastBlockRangeProcessed.Value);
                }

            } while (lastBlockRangeProcessed != null);

            return blocksProcessed;
        }


        protected virtual IBlockProgressService CreateProgressService(ulong from, ulong? to)
        {
            if (BlockProgressServiceCallBack != null) return BlockProgressServiceCallBack.Invoke(from, to);

            var progressRepository =
                new JsonBlockProgressRepository(PathToJsonProgressFile());

            IBlockProgressService progressService = null;
            if (to == null)
            {
                progressService = new BlockProgressService(BlockchainProxyService, from, progressRepository, MinimumBlockConfirmations);
            }
            else
            {
                progressService = new StaticBlockRangeProgressService(from, to.Value, progressRepository);
            }

            return progressService;
        }

        private string PathToJsonProgressFile()
        {
            var progressFileNameAndPath = Path.Combine(Path.GetTempPath(), $"{this.GetType().Name}_Progress.json");
            return progressFileNameAndPath;
        }

        public virtual Task ClearProgress()
        {
            var jsonFile = PathToJsonProgressFile();
            if (File.Exists(jsonFile)) File.Delete(jsonFile);
            return Task.CompletedTask;
        }

        public virtual void Dispose()
        {
            foreach (var processor in LogProcessors)
            {
                if (processor is IDisposable d)
                {
                    d.Dispose();
                }
            }

            SearchService?.Dispose();
        }

        protected virtual IEventIndexProcessor<TEvent> CreateProcessor<TEvent>(IEnumerable<ITransactionHandler> functionHandlers, IEventIndexer<TEvent> indexer) where TEvent : class, new()
        {
            var processor = new EventIndexProcessor<TEvent>(indexer, FunctionProcessor);
            LogProcessors.Add(processor);

            if (functionHandlers != null)
            {
                foreach (var functionHandler in functionHandlers)
                {
                    FunctionProcessor.AddHandler<TEvent>(functionHandler);
                }
            }

            return processor;
        }
    }
}
