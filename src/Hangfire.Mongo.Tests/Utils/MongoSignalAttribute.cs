﻿using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Mongo.Signal.Mongo;
using Xunit.Sdk;

namespace Hangfire.Mongo.Tests.Utils
{
    public class MongoSignalAttribute : BeforeAfterTestAttribute
    {
        private static long Count = 0;
        private static readonly object GlobalLock = new object();

        private MongoSignalBackgroundProcess _mongoSignalManager;
        private CancellationTokenSource _cancellationTokenSource;

        public override void Before(MethodInfo methodUnderTest)
        {

            lock (GlobalLock)
            {
                if (Count == 0)
                {
                    Count += 1;
                    Start();
                }
            }
        }

        public override void After(MethodInfo methodUnderTest)
        {
            lock (GlobalLock)
            {
                Count -= 1;
                if (Count == 0)
                {
                    Stop();
                }
            }
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }

        public void Start()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Task.Run(() =>
            {
                var cancellationToken = _cancellationTokenSource.Token;
                var signalCollection = ConnectionUtils.CreateStorage().Connection.Signal;
                _mongoSignalManager = new MongoSignalBackgroundProcess(signalCollection);

                while (!cancellationToken.IsCancellationRequested)
                {
                    _mongoSignalManager.Execute(cancellationToken);
                }

                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
                _mongoSignalManager = null;
            });
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
        }

    }
}
