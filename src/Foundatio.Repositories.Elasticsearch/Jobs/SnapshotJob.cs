﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless.DateTimeExtensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Parsers.ElasticQueries.Extensions;
using Foundatio.Repositories.Extensions;
using Foundatio.Utility;
using Nest;

namespace Foundatio.Repositories.Elasticsearch.Jobs {
    public class SnapshotJob : IJob {
        protected readonly IElasticClient _client;
        protected readonly ILockProvider _lockProvider;
        protected readonly ILogger _logger;

        public SnapshotJob(IElasticClient client, ILockProvider lockProvider, ILoggerFactory loggerFactory) {
            _client = client;
            _lockProvider = lockProvider;
            _logger = loggerFactory.CreateLogger(GetType());
        }

        public virtual async Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            string snapshotName = SystemClock.UtcNow.ToString("'" + Repository + "-'yyyy-MM-dd-HH-mm");
            _logger.Info($"Starting {Repository} snapshot {snapshotName}...");

            await _lockProvider.TryUsingAsync("es-snapshot", async t => {
                var sw = Stopwatch.StartNew();
                var result = await Run.WithRetriesAsync(async () => {
                    var response = await _client.SnapshotAsync(
                        Repository,
                        snapshotName,
                        d => d
                            .Indices(IncludedIndexes.Count > 0 ? String.Join(",", IncludedIndexes) : "*")
                            .IgnoreUnavailable()
                            .IncludeGlobalState(false)
                            .WaitForCompletion()
                            .RequestConfiguration(r => r.RequestTimeout(TimeSpan.FromMinutes(60)))
                        , cancellationToken).AnyContext();

                    // 400 means the snapshot already exists
                    if (!response.IsValid && response.ApiCall.HttpStatusCode != 400)
                        throw new ApplicationException($"Snapshot failed: {response.GetErrorMessage()}", response.OriginalException);

                    return response;
                },
                    maxAttempts: 5,
                    retryInterval: TimeSpan.FromSeconds(10),
                    cancellationToken: cancellationToken,
                    logger: _logger).AnyContext();
                sw.Stop();

                if (result.IsValid && result.ApiCall.HttpStatusCode != 400)
                    await OnSuccess(snapshotName, sw.Elapsed).AnyContext();
                else
                    await OnFailure(snapshotName, result).AnyContext();
            }, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)).AnyContext();

            return JobResult.Success;
        }

        public virtual Task OnSuccess(string snapshotName, TimeSpan duration) {
            _logger.Info($"Completed {Repository} snapshot {snapshotName} in {duration.ToWords(true)}");
            return Task.CompletedTask;
        }

        public virtual Task OnFailure(string snapshotName, ISnapshotResponse response) {
            _logger.Error($"Failed {Repository} snapshot {snapshotName}: {response.GetErrorMessage()}");
            return Task.CompletedTask;
        }

        protected string Repository { get; set; } = "data";
        protected ICollection<string> IncludedIndexes { get; } = new List<string>();
    }
}
