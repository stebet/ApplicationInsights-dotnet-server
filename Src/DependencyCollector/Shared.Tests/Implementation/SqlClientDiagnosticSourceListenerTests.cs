namespace Microsoft.ApplicationInsights.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.SqlClient;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.DependencyCollector.Implementation;
    using Microsoft.ApplicationInsights.DependencyCollector.Implementation.SqlClientDiagnostics;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation.Tracing;
    using Microsoft.ApplicationInsights.Web.TestFramework;
    using Xunit;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

    public class SqlClientDiagnosticSourceListenerTests : IDisposable
    {
        private const string TestConnectionString = "Data Source=(localdb)\\MSSQLLocalDB;Database=master";
        
        private IList<ITelemetry> sendItems;
        private StubTelemetryChannel stubTelemetryChannel;
        private TelemetryConfiguration configuration;
        private FakeSqlClientDiagnosticSource fakeSqlClientDiagnosticSource;
        private SqlClientDiagnosticSourceListener sqlClientDiagnosticSourceListener;

        public SqlClientDiagnosticSourceListenerTests()
        {
            this.sendItems = new List<ITelemetry>();
            this.stubTelemetryChannel = new StubTelemetryChannel { OnSend = item => this.sendItems.Add(item) };

            this.configuration = new TelemetryConfiguration
            {
                InstrumentationKey = Guid.NewGuid().ToString(),
                TelemetryChannel = this.stubTelemetryChannel
            };

            this.fakeSqlClientDiagnosticSource = new FakeSqlClientDiagnosticSource();
            this.sqlClientDiagnosticSourceListener = new SqlClientDiagnosticSourceListener(this.configuration);
        }

        public void Dispose()
        {
            this.sqlClientDiagnosticSourceListener.Dispose();
            this.fakeSqlClientDiagnosticSource.Dispose();
            this.configuration.Dispose();
            this.stubTelemetryChannel.Dispose();

            while (Activity.Current != null)
            {
                Activity.Current.Stop();
            }
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlAfterExecuteCommand)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlMicrosoftAfterExecuteCommand)]
        public void InitializesTelemetryFromParentActivity(string beforeEventName, string afterEventName)
        {
            var activity = new Activity("Current").AddBaggage("Stuff", "123");
            activity.Start();

            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = "select * from orders";

            var beforeExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = (long?)1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeEventName,
                beforeExecuteEventData);

            var afterExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = 2000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                afterEventName,
                afterExecuteEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(activity.RootId, dependencyTelemetry.Context.Operation.Id);
            Assert.Equal(activity.Id, dependencyTelemetry.Context.Operation.ParentId);
            Assert.Equal("123", dependencyTelemetry.Properties["Stuff"]);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlAfterExecuteCommand)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlMicrosoftAfterExecuteCommand)]
        public void TracksCommandExecuted(string beforeCommand, string afterCommand)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = "select * from orders";
            
            var beforeExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = (long?)1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeCommand,
                beforeExecuteEventData);
            var start = DateTimeOffset.UtcNow;

            var afterExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = 2000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                afterCommand,
                afterExecuteEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(beforeExecuteEventData.OperationId.ToString("N"), dependencyTelemetry.Id);
            Assert.Equal(sqlCommand.CommandText, dependencyTelemetry.Data);
            Assert.Equal("(localdb)\\MSSQLLocalDB | master", dependencyTelemetry.Name);
            Assert.Equal("(localdb)\\MSSQLLocalDB | master", dependencyTelemetry.Target);
            Assert.Equal(RemoteDependencyConstants.SQL, dependencyTelemetry.Type);
            Assert.True((bool)dependencyTelemetry.Success);
            Assert.True(dependencyTelemetry.Duration > TimeSpan.Zero);
            Assert.True(dependencyTelemetry.Duration < TimeSpan.FromMilliseconds(500));
            Assert.True(Math.Abs((start - dependencyTelemetry.Timestamp).TotalMilliseconds) <= 16);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlAfterExecuteCommand)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlMicrosoftAfterExecuteCommand)]
        public void TracksCommandExecutedWhenNoTimestamp(string beforeCommand, string afterCommand)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = "select * from orders";

            var beforeExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = (long?)null
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeCommand,
                beforeExecuteEventData);

            var afterExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = Stopwatch.GetTimestamp()
            };

            this.fakeSqlClientDiagnosticSource.Write(
                afterCommand,
                afterExecuteEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(beforeExecuteEventData.OperationId.ToString("N"), dependencyTelemetry.Id);
            Assert.Equal(sqlCommand.CommandText, dependencyTelemetry.Data);
            Assert.Equal("(localdb)\\MSSQLLocalDB | master", dependencyTelemetry.Name);
            Assert.Equal("(localdb)\\MSSQLLocalDB | master", dependencyTelemetry.Target);
            Assert.Equal(RemoteDependencyConstants.SQL, dependencyTelemetry.Type);
            Assert.True((bool)dependencyTelemetry.Success);
            Assert.True(dependencyTelemetry.Duration > TimeSpan.Zero);
            Assert.True(dependencyTelemetry.Duration < TimeSpan.FromMilliseconds(500));
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlErrorExecuteCommand)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlMicrosoftErrorExecuteCommand)]
        public void TracksCommandError(string beforeCommand, string errorCommand)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = "select * from orders";

            var beforeExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = (long?)1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeCommand,
                beforeExecuteEventData);

            var commandErrorEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Exception = new Exception("Boom!"),
                Timestamp = 2000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                errorCommand,
                commandErrorEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(commandErrorEventData.Exception.ToInvariantString(), dependencyTelemetry.Properties["Exception"]);
            Assert.False(dependencyTelemetry.Success.Value);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlErrorExecuteCommand)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeExecuteCommand, SqlClientDiagnosticSourceListener.SqlMicrosoftErrorExecuteCommand)]
        public void TracksCommandErrorWhenSqlException(string beforeCommand, string errorCommand)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = "select * from orders";

            var beforeExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = (long?)1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeCommand,
                beforeExecuteEventData);

            // Need to create SqlException via reflection because ctor is not public!
            var sqlErrorCtor
                = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Single(c => c.GetParameters().Count() == 8);

            var sqlError = sqlErrorCtor.Invoke(
                new object[]
                {
                    42, // error number
                    default(byte),
                    default(byte),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    default(Exception)
                });

            var sqlErrorCollectionCtor
                = typeof(SqlErrorCollection).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Single();

            var sqlErrorCollection = sqlErrorCollectionCtor.Invoke(new object[] { });

            typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(sqlErrorCollection, new object[] { sqlError });
            
            var sqlExceptionCtor = typeof(SqlException).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];

            var sqlException = (SqlException)sqlExceptionCtor.Invoke(
                new object[]
                {
                    "Boom!",
                    sqlErrorCollection,
                    null,
                    Guid.NewGuid()
                });

            var commandErrorEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Exception = (Exception)sqlException,
                Timestamp = 2000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                errorCommand,
                commandErrorEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(commandErrorEventData.Exception.ToInvariantString(), dependencyTelemetry.Properties["Exception"]);
            Assert.False(dependencyTelemetry.Success.Value);
            Assert.Equal("42", dependencyTelemetry.ResultCode);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeOpenConnection, SqlClientDiagnosticSourceListener.SqlErrorOpenConnection)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeOpenConnection, SqlClientDiagnosticSourceListener.SqlMicrosoftErrorOpenConnection)]
        public void TracksConnectionOpenedError(string openCon, string openError)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            
            var beforeOpenEventData = new
            {
                OperationId = operationId,
                Operation = "Open",
                Connection = sqlConnection,
                Timestamp = 1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                openCon,
                beforeOpenEventData);

            var errorOpenEventData = new
            {
                OperationId = operationId,
                Connection = sqlConnection,
                Exception = new Exception("Boom!"),
                Timestamp = 2000000L
            };

            var start = DateTimeOffset.UtcNow;

            this.fakeSqlClientDiagnosticSource.Write(
                openError,
                errorOpenEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(beforeOpenEventData.OperationId.ToString("N"), dependencyTelemetry.Id);
            Assert.Equal(beforeOpenEventData.Operation, dependencyTelemetry.Data);
            Assert.Equal("(localdb)\\MSSQLLocalDB | master | " + beforeOpenEventData.Operation, dependencyTelemetry.Name);
            Assert.Equal("(localdb)\\MSSQLLocalDB | master", dependencyTelemetry.Target);
            Assert.Equal(RemoteDependencyConstants.SQL, dependencyTelemetry.Type);
            Assert.True(dependencyTelemetry.Duration > TimeSpan.Zero);
            Assert.True(dependencyTelemetry.Duration < TimeSpan.FromMilliseconds(500));
            Assert.True(Math.Abs((start - dependencyTelemetry.Timestamp).TotalMilliseconds) <= 16);

            Assert.Equal(errorOpenEventData.Exception.ToInvariantString(), dependencyTelemetry.Properties["Exception"]);
            Assert.False(dependencyTelemetry.Success.Value);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeOpenConnection, SqlClientDiagnosticSourceListener.SqlAfterOpenConnection)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeOpenConnection, SqlClientDiagnosticSourceListener.SqlMicrosoftAfterOpenConnection)]
        public void DoesNotTrackConnectionOpened(string beforeOpen, string afterOpen)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);

            var beforeOpenEventData = new
            {
                OperationId = operationId,
                Operation = "Open",
                Connection = sqlConnection,
                Timestamp = 1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeOpen,
                beforeOpenEventData);

            var afterOpenEventData = new
            {
                OperationId = operationId,
                Connection = sqlConnection
            };

            this.fakeSqlClientDiagnosticSource.Write(
                afterOpen,
                afterOpenEventData);

            Assert.Equal(0, this.sendItems.Count);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeCloseConnection, SqlClientDiagnosticSourceListener.SqlErrorCloseConnection)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeCloseConnection, SqlClientDiagnosticSourceListener.SqlMicrosoftErrorCloseConnection)]
        public void DoesNotTrackConnectionCloseError(string beforeClose, string errorClose)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);

            var beforeOpenEventData = new
            {
                OperationId = operationId,
                Operation = "Close",
                Connection = sqlConnection,
                Timestamp = 1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeClose,
                beforeOpenEventData);

            var errorCloseEventData = new
            {
                OperationId = operationId,
                Connection = sqlConnection,
                Exception = new Exception("Boom!"),
                Timestamp = 2000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                errorClose,
                errorCloseEventData);

            Assert.Equal(0, this.sendItems.Count);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeCommitTransaction, SqlClientDiagnosticSourceListener.SqlAfterCommitTransaction)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeCommitTransaction, SqlClientDiagnosticSourceListener.SqlMicrosoftAfterCommitTransaction)]
        public void TracksTransactionCommitted(string beforeCommit, string afterCommit)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);

            var beforeCommitEventData = new
            {
                OperationId = operationId,
                Operation = "Commit",
                IsolationLevel = IsolationLevel.Snapshot,
                Connection = sqlConnection,
                Timestamp = 1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeCommit,
                beforeCommitEventData);

            var afterCommitEventData = new
            {
                OperationId = operationId,
                Connection = sqlConnection,
                Timestamp = 2000000L
            };

            var start = DateTimeOffset.UtcNow;

            this.fakeSqlClientDiagnosticSource.Write(
                afterCommit,
                afterCommitEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(beforeCommitEventData.OperationId.ToString("N"), dependencyTelemetry.Id);
            Assert.Equal(beforeCommitEventData.Operation, dependencyTelemetry.Data);
            Assert.Equal(
                "(localdb)\\MSSQLLocalDB | master | " + beforeCommitEventData.Operation + " | " + beforeCommitEventData.IsolationLevel, 
                dependencyTelemetry.Name);
            Assert.Equal("(localdb)\\MSSQLLocalDB | master", dependencyTelemetry.Target);
            Assert.Equal(RemoteDependencyConstants.SQL, dependencyTelemetry.Type);
            Assert.True((bool)dependencyTelemetry.Success);
            Assert.True(dependencyTelemetry.Duration > TimeSpan.Zero);
            Assert.True(dependencyTelemetry.Duration < TimeSpan.FromMilliseconds(500));
            Assert.True(Math.Abs((start - dependencyTelemetry.Timestamp).TotalMilliseconds) <= 16);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeCommitTransaction, SqlClientDiagnosticSourceListener.SqlErrorCommitTransaction)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeCommitTransaction, SqlClientDiagnosticSourceListener.SqlMicrosoftErrorCommitTransaction)]
        public void TracksTransactionCommitError(string beforeCommit, string afterCommit)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);

            var beforeCommitEventData = new
            {
                OperationId = operationId,
                Operation = "Commit",
                IsolationLevel = IsolationLevel.Snapshot,
                Connection = sqlConnection,
                Timestamp = 1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeCommit,
                beforeCommitEventData);

            var errorCommitEventData = new
            {
                OperationId = operationId,
                Connection = sqlConnection,
                Exception = new Exception("Boom!"),
                Timestamp = 2000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                afterCommit,
                errorCommitEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(errorCommitEventData.Exception.ToInvariantString(), dependencyTelemetry.Properties["Exception"]);
            Assert.False(dependencyTelemetry.Success.Value);
        }

        [Theory]
        [InlineData(SqlClientDiagnosticSourceListener.SqlBeforeRollbackTransaction, SqlClientDiagnosticSourceListener.SqlAfterRollbackTransaction)]
        [InlineData(SqlClientDiagnosticSourceListener.SqlMicrosoftBeforeRollbackTransaction, SqlClientDiagnosticSourceListener.SqlMicrosoftAfterRollbackTransaction)]
        public void TracksTransactionRolledBack(string beforeCommit, string afterCommit)
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);

            var beforeRollbackEventData = new
            {
                OperationId = operationId,
                Operation = "Rollback",
                IsolationLevel = IsolationLevel.Snapshot,
                TransactionName = "testTransactionName",
                Connection = sqlConnection,
                Timestamp = 1000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                beforeCommit,
                beforeRollbackEventData);

            var afterRollbackEventData = new
            {
                OperationId = operationId,
                Connection = sqlConnection,
                Timestamp = 2000000L
            };

            var start = DateTimeOffset.UtcNow;

            this.fakeSqlClientDiagnosticSource.Write(
                afterCommit,
                afterRollbackEventData);

            var dependencyTelemetry = (DependencyTelemetry)this.sendItems.Single();

            Assert.Equal(beforeRollbackEventData.OperationId.ToString("N"), dependencyTelemetry.Id);
            Assert.Equal(beforeRollbackEventData.Operation, dependencyTelemetry.Data);
            Assert.Equal(
                "(localdb)\\MSSQLLocalDB | master | " + beforeRollbackEventData.Operation + " | " + beforeRollbackEventData.IsolationLevel,
                dependencyTelemetry.Name);
            Assert.Equal("(localdb)\\MSSQLLocalDB | master", dependencyTelemetry.Target);
            Assert.Equal(RemoteDependencyConstants.SQL, dependencyTelemetry.Type);
            Assert.True((bool)dependencyTelemetry.Success);
            Assert.True(dependencyTelemetry.Duration > TimeSpan.Zero);
            Assert.True(dependencyTelemetry.Duration < TimeSpan.FromMilliseconds(500));
            Assert.True(Math.Abs((start - dependencyTelemetry.Timestamp).TotalMilliseconds) <= 16);
        }

        [Fact]
        public void MultiHost_OneListenerIsActive()
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = "select * from orders";

            var beforeExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = (long?)1000000L
            };

            var afterExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = 2000000L
            };

            using (var listener2 = new SqlClientDiagnosticSourceListener(this.configuration))
            {
                this.fakeSqlClientDiagnosticSource.Write(
                    SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand,
                    beforeExecuteEventData);

                this.fakeSqlClientDiagnosticSource.Write(
                    SqlClientDiagnosticSourceListener.SqlAfterExecuteCommand,
                    afterExecuteEventData);

                Assert.Equal(1, this.sendItems.Count(t => t is DependencyTelemetry));
            }
        }

        [Fact]
        public void MultiHost_OneListenerThenAnotherIsActive()
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = "select * from orders";

            var beforeExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = (long?)1000000L
            };

            var afterExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = 2000000L
            };

            this.fakeSqlClientDiagnosticSource.Write(
                SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand,
                beforeExecuteEventData);

            this.fakeSqlClientDiagnosticSource.Write(
                SqlClientDiagnosticSourceListener.SqlAfterExecuteCommand,
                afterExecuteEventData);

            Assert.Equal(1, this.sendItems.Count(t => t is DependencyTelemetry));

            this.sqlClientDiagnosticSourceListener.Dispose();

            using (var listener = new SqlClientDiagnosticSourceListener(this.configuration))
            {
                this.fakeSqlClientDiagnosticSource.Write(
                    SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand,
                    beforeExecuteEventData);

                this.fakeSqlClientDiagnosticSource.Write(
                    SqlClientDiagnosticSourceListener.SqlAfterExecuteCommand,
                    afterExecuteEventData);

                Assert.Equal(2, this.sendItems.Count(t => t is DependencyTelemetry));
            }
        }

        [Fact]
        public void MultiHost_OneListnerIsActiveAfterDispose()
        {
            var operationId = Guid.NewGuid();
            var sqlConnection = new SqlConnection(TestConnectionString);
            var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = "select * from orders";

            var beforeExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = (long?)1000000L
            };

            var afterExecuteEventData = new
            {
                OperationId = operationId,
                Command = sqlCommand,
                Timestamp = 2000000L
            };

            using (var listener2 = new SqlClientDiagnosticSourceListener(this.configuration))
            {
                this.fakeSqlClientDiagnosticSource.Write(
                    SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand,
                    beforeExecuteEventData);

                this.fakeSqlClientDiagnosticSource.Write(
                    SqlClientDiagnosticSourceListener.SqlAfterExecuteCommand,
                    afterExecuteEventData);

                Assert.Equal(1, this.sendItems.Count(t => t is DependencyTelemetry));

                this.sqlClientDiagnosticSourceListener.Dispose();

                this.fakeSqlClientDiagnosticSource.Write(
                    SqlClientDiagnosticSourceListener.SqlBeforeExecuteCommand,
                    beforeExecuteEventData);

                this.fakeSqlClientDiagnosticSource.Write(
                    SqlClientDiagnosticSourceListener.SqlAfterExecuteCommand,
                    afterExecuteEventData);

                Assert.Equal(2, this.sendItems.Count(t => t is DependencyTelemetry));
            }
        }

        private class FakeSqlClientDiagnosticSource : IDisposable
        {
            private readonly DiagnosticListener listener;

            public FakeSqlClientDiagnosticSource()
            {
                this.listener = new DiagnosticListener(SqlClientDiagnosticSourceListener.DiagnosticListenerName);
            }

            public void Write(string name, object value)
            {
                this.listener.Write(name, value);
            }

            public void Dispose()
            {
                this.listener.Dispose();
            }
        }
    }

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}