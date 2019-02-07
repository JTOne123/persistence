﻿using System;
using System.Collections.Concurrent;
using Citolab.Persistence.Decorators;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Citolab.Persistence.MongoDb
{
    /// <summary>
    ///     Unit of Work for mongo database
    /// </summary>
    public class MongoDbUnitOfWork : UnitOfWorkBase
    {
        private readonly ILogger _logger;
        private readonly IMongoDatabase _mongoDatabase;
        private readonly IClientSession _clientSession;

        /// <summary>
        ///     Constructor
        /// </summary>
        /// <param name="memoryCache"></param>
        /// <param name="loggerFactory"></param>
        /// <param name="options"></param>
        public MongoDbUnitOfWork(IMemoryCache memoryCache, ILoggerFactory loggerFactory,
            ICollectionOptions options)
            : base(memoryCache, loggerFactory, options)
        {
            if (!(options is IMongoDbDatabaseOptions mongoOptions))
                throw new Exception("Options should be of type IMongoDatabaseOptions");
            _logger = loggerFactory.CreateLogger(GetType());
            try
            {
                MongoClient client;
                try
                {
                    var mongoClientSettings = new MongoClientSettings
                    {
                        Server = MongoServerAddress.Parse(mongoOptions.ConnectionString)
                    };
                    client = new MongoClient(mongoClientSettings);
                }
                catch
                {
                    client = new MongoClient(mongoOptions.ConnectionString);
                }

                var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                if (string.IsNullOrWhiteSpace(environment))
                {
                    environment = "Development";
                }

                _mongoDatabase = client.GetDatabase($"{mongoOptions.DatabaseName}-{environment}");
                _clientSession = _mongoDatabase.Client.StartSession();
                _clientSession.StartTransaction();
                Collections = new ConcurrentDictionary<Type, object>();
            }
            catch (Exception exception)
            {
                _logger.LogCritical(
                    $"Error while connecting to {mongoOptions.ConnectionString}.", exception);
                throw;
            }
        }

        /// <summary>
        ///     Collections
        /// </summary>
        private ConcurrentDictionary<Type, object> Collections { get; set; }

        /// <inheritdoc />
        public override ICollection<T> GetCollection<T>()
        {
            if (Collections.TryGetValue(typeof(T), out var collection))
            {
                return (ICollection<T>) collection;
            }

            var mongoCollection = new FlagAsDeletedDecorator<T>(MemoryCache,
                new FillDefaultValueDecorator<T>(MemoryCache,
                    new CacheDecorator<T>(MemoryCache, false,
                        new MongoDbCollection<T>(LoggerFactory, _mongoDatabase)), ActorId));
            if (LogTime)
            {
                var timeLoggedMongoDbCollection = new LogTimeDecorator<T>(MemoryCache, mongoCollection, _logger);
                Collections.TryAdd(typeof(T), timeLoggedMongoDbCollection);
            }
            else
            {
                Collections.TryAdd(typeof(T), mongoCollection);
            }

            return mongoCollection;
        }

        /// <inheritdoc />
        public override void Commit()
        {
            while (true)
            {
                try
                {
                    _clientSession.CommitTransaction();
                    _logger.LogInformation("Transaction committed.");
                    break;
                }
                catch (MongoException exception)
                {
                    // can retry commit
                    if (exception.HasErrorLabel("UnknownTransactionCommitResult"))
                    {
                        _logger.LogWarning("UnknownTransactionCommitResult, retrying commit operation");
                    }
                    else
                    {
                        _logger.LogError($"Error during commit: {exception.Message}.");
                        throw;
                    }
                }
            }
        }

        /// <inheritdoc />
        public override void Abort()
        {
            _clientSession.AbortTransaction();
        }
    }
}