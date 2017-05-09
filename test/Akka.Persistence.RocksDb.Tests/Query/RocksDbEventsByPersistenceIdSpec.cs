﻿using System;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence.Query;
using Akka.Persistence.Query.RocksDb;
using Akka.Persistence.RocksDb.Tests.TestKit;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.Util.Internal;
using FluentAssertions;
using Xunit;

namespace Akka.Persistence.RocksDb.Tests.Query
{
    public class EventsByPersistenceIdSpec : Akka.TestKit.Xunit2.TestKit
    {
        public static readonly AtomicCounter Counter = new AtomicCounter(0);

        public static Config Config(int id) => ConfigurationFactory.ParseString($@"
            akka.loglevel = INFO
            akka.persistence.journal.plugin = ""akka.persistence.journal.rocksdb""
            akka.persistence.journal.rocksdb {{
                class = ""Akka.Persistence.RocksDb.Journal.RocksDbJournal, Akka.Persistence.RocksDb""
                plugin-dispatcher = ""akka.actor.default-dispatcher""
                auto-initialize = on
                path = rocks_events_{id}.db
            }}
            akka.test.single-expect-default = 3s")
            .WithFallback(RocksDbReadJournal.DefaultConfiguration());

        private readonly ActorMaterializer _materializer;

        public EventsByPersistenceIdSpec() : base(Config(Counter.GetAndIncrement()))
        {
            _materializer = Sys.Materializer();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_implement_standard_EventsByTagQuery()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            queries.Should().BeAssignableTo<IEventsByPersistenceIdQuery>();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_find_existing_events()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("a");

            var src = queries.CurrentEventsByPersistenceId("a", 0, long.MaxValue);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer);
            probe.Request(2)
                .ExpectNext("a-1", "a-2")
                .ExpectNoMsg(TimeSpan.FromMilliseconds(500));
            probe.Request(2)
                .ExpectNext("a-3")
                .ExpectComplete();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_find_existing_events_up_to_a_sequence_number()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("b");
            var src = queries.CurrentEventsByPersistenceId("b", 0L, 2L);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer)
                .Request(5)
                .ExpectNext("b-1", "b-2")
                .ExpectComplete();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_not_see_new_events_after_demand_request()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("f");
            var src = queries.CurrentEventsByPersistenceId("f", 0L, long.MaxValue);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer)
                .Request(2)
                .ExpectNext("f-1", "f-2")
                .ExpectNoMsg(TimeSpan.FromMilliseconds(100)) as TestSubscriber.Probe<object>;

            pref.Tell("f-4");
            ExpectMsg("f-4-done");

            probe.ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            probe.Request(5)
                .ExpectNext("f-3")
                .ExpectComplete(); // f-4 not seen
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_return_empty_stream_for_cleaned_journal_from_0_to_MaxLong()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("g1");

            pref.Tell(new TestActor.DeleteCommand(3));
            AwaitAssert(() => ExpectMsg("3-deleted"));

            var src = queries.CurrentEventsByPersistenceId("g1", 0, long.MaxValue);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_return_empty_stream_for_cleaned_journal_from_0_to_0()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("g2");

            pref.Tell(new TestActor.DeleteCommand(3));
            AwaitAssert(() => ExpectMsg("3-deleted"));

            var src = queries.CurrentEventsByPersistenceId("g2", 0, 0);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_return_remaining_values_after_partial_journal_cleanup()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("h");

            pref.Tell(new TestActor.DeleteCommand(2));
            AwaitAssert(() => ExpectMsg("2-deleted"));

            var src = queries.CurrentEventsByPersistenceId("h", 0, long.MaxValue);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer).Request(1).ExpectNext("h-3").ExpectComplete();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_return_empty_stream_for_empty_journal()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = SetupEmpty("i");

            var src = queries.CurrentEventsByPersistenceId("i", 0L, long.MaxValue);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_return_empty_stream_for_journal_from_0_to_0()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("k1");

            var src = queries.CurrentEventsByPersistenceId("k1", 0, 0);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_return_empty_stream_for_empty_journal_from_0_to_0()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = SetupEmpty("k2");

            var src = queries.CurrentEventsByPersistenceId("k2", 0, 0);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer).Request(1).ExpectComplete();
        }

        [Fact]
        public void Sql_query_EventsByPersistenceId_should_return_empty_stream_for_journal_from_SequenceNr_greater_than_HighestSequenceNr()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("l");

            var src = queries.CurrentEventsByPersistenceId("l", 4, 3);
            src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer).Request(1).ExpectComplete();

        }

        [Fact]
        public void Sql_live_query_EventsByPersistenceId_should_find_new_events()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("c");

            var src = queries.EventsByPersistenceId("c", 0, long.MaxValue);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer)
                .Request(5)
                .ExpectNext("c-1", "c-2", "c-3");

            pref.Tell("c-4");
            ExpectMsg("c-4-done");

            probe.ExpectNext("c-4");
        }

        [Fact]
        public void Sql_live_query_EventsByPersistenceId_should_find_new_events_up_to_SequenceNr()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("d");

            var src = queries.EventsByPersistenceId("d", 0, 4);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer)
                .Request(5)
                .ExpectNext("d-1", "d-2", "d-3");

            pref.Tell("d-4");
            ExpectMsg("d-4-done");

            probe.ExpectNext("d-4").ExpectComplete();
        }

        [Fact]
        public void Sql_live_query_EventsByPersistenceId_should_find_new_events_after_demand_request()
        {
            var queries = Sys.ReadJournalFor<RocksDbReadJournal>(RocksDbReadJournal.Identifier);
            var pref = Setup("e");

            var src = queries.EventsByPersistenceId("e", 0, long.MaxValue);
            var probe = src.Select(x => x.Event).RunWith(this.SinkProbe<object>(), _materializer)
                .Request(2)
                .ExpectNext("e-1", "e-2")
                .ExpectNoMsg(TimeSpan.FromMilliseconds(100)) as TestSubscriber.Probe<object>;

            pref.Tell("e-4");
            ExpectMsg("e-4-done");

            probe.ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            probe.Request(5)
                .ExpectNext("e-3")
                .ExpectNext("e-4");
        }

        private IActorRef Setup(string persistenceId)
        {
            var pref = SetupEmpty(persistenceId);

            pref.Tell(persistenceId + "-1");
            pref.Tell(persistenceId + "-2");
            pref.Tell(persistenceId + "-3");

            ExpectMsg(persistenceId + "-1-done");
            ExpectMsg(persistenceId + "-2-done");
            ExpectMsg(persistenceId + "-3-done");
            return pref;
        }

        private IActorRef SetupEmpty(string persistenceId)
        {
            return Sys.ActorOf(TestKit.TestActor.Props(persistenceId));
        }

        protected override void Dispose(bool disposing)
        {
            _materializer.Dispose();
            base.Dispose(disposing);
        }
    }
}
