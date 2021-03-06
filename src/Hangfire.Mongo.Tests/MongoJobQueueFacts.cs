﻿using System;
using System.Linq;
using System.Threading;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.PersistentJobQueue.Mongo;
using Hangfire.Mongo.Tests.Utils;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Hangfire.Mongo.Tests
{
    [Collection("Database")]
    public class MongoJobQueueFacts
    {
        private static readonly string[] DefaultQueues = { "default" };

        [Fact]
        public void Ctor_ThrowsAnException_WhenConnectionIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new MongoJobQueue(null, null));

            Assert.Equal("database", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenOptionsValueIsNull()
        {
            ConnectionUtils.UseConnection(database =>
            {
                var exception = Assert.Throws<ArgumentNullException>(() =>
                    new MongoJobQueue(database, null));

                Assert.Equal("storageOptions", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldThrowAnException_WhenQueuesCollectionIsNull()
        {
            ConnectionUtils.UseConnection(database =>
            {
                var queue = CreateJobQueue(database);

                var exception = Assert.Throws<ArgumentNullException>(() =>
                    queue.Dequeue(null, CreateTimingOutCancellationToken()));

                Assert.Equal("queues", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldThrowAnException_WhenQueuesCollectionIsEmpty()
        {
            ConnectionUtils.UseConnection(database =>
            {
                var queue = CreateJobQueue(database);

                var exception = Assert.Throws<ArgumentException>(() =>
                    queue.Dequeue(new string[0], CreateTimingOutCancellationToken()));

                Assert.Equal("queues", exception.ParamName);
            });
        }

        [Fact]
        public void Dequeue_ThrowsOperationCanceled_WhenCancellationTokenIsSetAtTheBeginning()
        {
            ConnectionUtils.UseConnection(database =>
            {
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    var queue = CreateJobQueue(database);

                    Assert.Throws<OperationCanceledException>(() =>
                        queue.Dequeue(DefaultQueues, cts.Token));
                }
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldWaitIndefinitely_WhenThereAreNoJobs()
        {
            ConnectionUtils.UseConnection(database =>
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                {
                    var queue = CreateJobQueue(database);

                    Assert.Throws<OperationCanceledException>(() =>
                        queue.Dequeue(DefaultQueues, cts.Token));
                }
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldFetchAJob_FromTheSpecifiedQueue()
        {
            // Arrange
            ConnectionUtils.UseConnection(database =>
            {
                var jobQueue = new JobQueueDto
                {
                    JobId = ObjectId.GenerateNewId(),
                    Queue = "default"
                };

                database.JobQueue.InsertOne(jobQueue);

                var queue = CreateJobQueue(database);

                // Act
                MongoFetchedJob payload = (MongoFetchedJob)queue.Dequeue(DefaultQueues, CreateTimingOutCancellationToken());

                // Assert
                Assert.Equal(jobQueue.JobId.ToString(), payload.JobId);
                Assert.Equal("default", payload.Queue);
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldLeaveJobInTheQueue_ButSetItsFetchedAtValue()
        {
            // Arrange
            ConnectionUtils.UseConnection(database =>
            {
                var job = new JobDto
                {
                    InvocationData = "",
                    Arguments = "",
                    CreatedAt = DateTime.UtcNow
                };
                database.Job.InsertOne(job);

                var jobQueue = new JobQueueDto
                {
                    JobId = job.Id,
                    Queue = "default"
                };
                database.JobQueue.InsertOne(jobQueue);

                var queue = CreateJobQueue(database);

                // Act
                var payload = queue.Dequeue(DefaultQueues, CreateTimingOutCancellationToken());

                // Assert
                Assert.NotNull(payload);

                var fetchedAt = database.JobQueue
                    .Find(Builders<JobQueueDto>.Filter.Eq(_ => _.JobId, ObjectId.Parse(payload.JobId)))
                    .FirstOrDefault()
                    .FetchedAt;

                Assert.NotNull(fetchedAt);
                Assert.True(fetchedAt > DateTime.UtcNow.AddMinutes(-1));
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldFetchATimedOutJobs_FromTheSpecifiedQueue()
        {
            // Arrange
            ConnectionUtils.UseConnection(database =>
            {
                var job = new JobDto
                {
                    InvocationData = "",
                    Arguments = "",
                    CreatedAt = DateTime.UtcNow
                };
                database.Job.InsertOne(job);

                var jobQueue = new JobQueueDto
                {
                    JobId = job.Id,
                    Queue = "default",
                    FetchedAt = DateTime.UtcNow.AddDays(-1)
                };
                database.JobQueue.InsertOne(jobQueue);

                var queue = CreateJobQueue(database);

                // Act
                var payload = queue.Dequeue(DefaultQueues, CreateTimingOutCancellationToken());

                // Assert
                Assert.NotEmpty(payload.JobId);
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldSetFetchedAt_OnlyForTheFetchedJob()
        {
            // Arrange
            ConnectionUtils.UseConnection(database =>
            {
                var job1 = new JobDto
                {
                    InvocationData = "",
                    Arguments = "",
                    CreatedAt = DateTime.UtcNow
                };
                database.Job.InsertOne(job1);

                var job2 = new JobDto
                {
                    InvocationData = "",
                    Arguments = "",
                    CreatedAt = DateTime.UtcNow
                };
                database.Job.InsertOne(job2);

                database.JobQueue.InsertOne(new JobQueueDto
                {
                    JobId = job1.Id,
                    Queue = "default"
                });

                database.JobQueue.InsertOne(new JobQueueDto
                {
                    JobId = job2.Id,
                    Queue = "default"
                });

                var queue = CreateJobQueue(database);

                // Act
                var payload = queue.Dequeue(DefaultQueues, CreateTimingOutCancellationToken());

                // Assert
                var otherJobFetchedAt = database
                    .JobQueue.Find(Builders<JobQueueDto>.Filter.Ne(_ => _.JobId, ObjectId.Parse(payload.JobId)))
                    .FirstOrDefault()
                    .FetchedAt;

                Assert.Null(otherJobFetchedAt);
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldFetchJobs_OnlyFromSpecifiedQueues()
        {
            ConnectionUtils.UseConnection(database =>
            {
                var job1 = new JobDto
                {
                    InvocationData = "",
                    Arguments = "",
                    CreatedAt = DateTime.UtcNow
                };
                database.Job.InsertOne(job1);

                database.JobQueue.InsertOne(new JobQueueDto
                {
                    JobId = job1.Id,
                    Queue = "critical"
                });


                var queue = CreateJobQueue(database);

                Assert.Throws<OperationCanceledException>(() => queue.Dequeue(DefaultQueues, CreateTimingOutCancellationToken()));
            });
        }

        [Fact, CleanDatabase]
        public void Dequeue_ShouldFetchJobs_FromMultipleQueuesBasedOnQueuePriority()
        {
            ConnectionUtils.UseConnection(database =>
            {
                var criticalJob = new JobDto
                {
                    InvocationData = "",
                    Arguments = "",
                    CreatedAt = DateTime.UtcNow
                };
                database.Job.InsertOne(criticalJob);

                var defaultJob = new JobDto
                {
                    InvocationData = "",
                    Arguments = "",
                    CreatedAt = DateTime.UtcNow
                };
                database.Job.InsertOne(defaultJob);

                database.JobQueue.InsertOne(new JobQueueDto
                {
                    JobId = defaultJob.Id,
                    Queue = "default"
                });

                database.JobQueue.InsertOne(new JobQueueDto
                {
                    JobId = criticalJob.Id,
                    Queue = "critical"
                });

                var queue = CreateJobQueue(database);

                var critical = (MongoFetchedJob)queue.Dequeue(
                    new[] { "critical", "default" },
                    CreateTimingOutCancellationToken());

                Assert.NotNull(critical.JobId);
                Assert.Equal("critical", critical.Queue);

                var @default = (MongoFetchedJob)queue.Dequeue(
                    new[] { "critical", "default" },
                    CreateTimingOutCancellationToken());

                Assert.NotNull(@default.JobId);
                Assert.Equal("default", @default.Queue);
            });
        }

        [Fact, CleanDatabase]
        public void Enqueue_AddsAJobToTheQueue()
        {
            ConnectionUtils.UseConnection(database =>
            {
                var queue = CreateJobQueue(database);
                var jobId = ObjectId.GenerateNewId().ToString();
                queue.Enqueue("default", jobId);

                var record = database.JobQueue.Find(new BsonDocument()).ToList().Single();
                Assert.Equal(jobId, record.JobId.ToString());
                Assert.Equal("default", record.Queue);
                Assert.Null(record.FetchedAt);
            });
        }

        private static CancellationToken CreateTimingOutCancellationToken()
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return cts.Token;
        }

        private static MongoJobQueue CreateJobQueue(HangfireDbContext database)
        {
            return new MongoJobQueue(database, new MongoStorageOptions());
        }

    }
}