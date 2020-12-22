﻿//-----------------------------------------------------------------------
// <copyright file="AllEventsSource.cs" company="Akka.NET Project">
//     Copyright (C) 2017 Akka.NET Contrib <https://github.com/AkkaNetContrib/Akka.Persistence.Redis>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.Configuration;
using Akka.Pattern;
using Akka.Persistence.Query;
using Akka.Streams;
using Akka.Streams.Stage;
using StackExchange.Redis;

namespace Akka.Persistence.Redis.Query.Stages
{
    internal class AllEventsSource : GraphStage<SourceShape<EventEnvelope>>
    {
        private readonly ConnectionMultiplexer _redis;
        private readonly int _database;
        private readonly Config _config;
        private readonly long _offset;
        private readonly ExtendedActorSystem _system;
        private readonly bool _live;

        public AllEventsSource(ConnectionMultiplexer redis, int database, Config config, long offset, ExtendedActorSystem system, bool live)
        {
            _redis = redis;
            _database = database;
            _config = config;
            _offset = offset;
            _system = system;
            _live = live;

            Outlet = live
                ? new Outlet<EventEnvelope>("AllEventsSource")
                : new Outlet<EventEnvelope>("CurrentAllEventsSource");

            Shape = new SourceShape<EventEnvelope>(Outlet);
        }

        internal Outlet<EventEnvelope> Outlet { get; }

        public override SourceShape<EventEnvelope> Shape { get; }

        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes)
        {
            return new AllEventsLogic(_redis, _database, _config, _system, _offset, _live, Outlet, Shape);
        }

        private enum State
        {
            Idle = 0,
            Querying = 1,
            NotifiedWhenQuerying = 2,
            WaitingForNotification = 3,
            Initializing = 4,
            QueryWhenInitializing = 5
        }

        private class AllEventsLogic : GraphStageLogic
        {
            private State _state = State.Idle;
            private readonly Queue<EventEnvelope> _buffer = new Queue<EventEnvelope>();
            private ISubscriber _subscription;
            private readonly int _max;
            private readonly JournalHelper _journalHelper;
            private Action<(int, IReadOnlyList<(string, IPersistentRepresentation)>)> _callback;

            private readonly Outlet<EventEnvelope> _outlet;
            private readonly ConnectionMultiplexer _redis;
            private readonly int _database;
            private readonly ActorSystem _system;
            private readonly long _offset;
            private readonly bool _live;

            private long _currentOffset;
            private long _maxOffset = long.MaxValue;

            public AllEventsLogic(
                ConnectionMultiplexer redis,
                int database,
                Config config,
                ActorSystem system,
                long offset,
                bool live,
                Outlet<EventEnvelope> outlet, Shape shape) : base(shape)
            {
                _outlet = outlet;
                _redis = redis;
                _database = database;
                _system = system;
                _offset = offset;
                _live = live;

                _max = config.GetInt("max-buffer-size");
                _journalHelper = new JournalHelper(system, system.Settings.Config.GetString("akka.persistence.journal.redis.key-prefix"));

                _currentOffset = offset > 0 ? offset + 1 : 0;

                SetHandler(outlet, onPull: () =>
                {
                    switch (_state)
                    {
                        case State.Initializing:
                            _state = State.QueryWhenInitializing;
                            break;
                        default:
                            Query();
                            break;
                    }
                });
            }

            public override void PreStart()
            {
                _callback = GetAsyncCallback<(int, IReadOnlyList<(string, IPersistentRepresentation)>)>(raw =>
                {
                    var nb = raw.Item1;
                    var events = raw.Item2;

                    if (events.Count == 0)
                    {
                        if (_currentOffset >= _maxOffset)
                        {
                            // end has been reached
                            CompleteStage();
                        }
                        else
                        {
                            switch (_state)
                            {
                                case State.NotifiedWhenQuerying:
                                    // maybe we missed some new event when querying, retry
                                    _state = State.Idle;
                                    Query();
                                    break;
                                case State.Querying:
                                    if (_live)
                                    {
                                        // nothing new, wait for notification
                                        _state = State.WaitingForNotification;
                                    }
                                    else
                                    {
                                        // not a live stream, nothing else currently in the database, close the stream
                                        CompleteStage();
                                    }
                                    break;
                                default:
                                    Log.Error($"Unexpected source state: {_state}");
                                    FailStage(new IllegalStateException($"Unexpected source state: {_state}"));
                                    break;
                            }
                        }
                    }
                    else
                    {
                        var evts = events.ZipWithIndex()
                        .Where(kvp => kvp.Key.Item2 != null && !kvp.Key.Item2.IsDeleted)
                        .Select(c =>
                        {
                            var repr = c.Key.Item2;
                            return new EventEnvelope(new Sequence(_currentOffset + c.Value), repr.PersistenceId, repr.SequenceNr, repr.Payload);
                        }).ToList();

                        _currentOffset += nb;
                        if (evts.Count > 0)
                        {
                            evts.ForEach(_buffer.Enqueue);
                            Deliver();
                        }
                        else
                        {
                            // requery immediately
                            _state = State.Idle;
                            Query();
                        }
                    }
                });

                if (_live)
                {
                    // subscribe to notification stream only if live stream was required
                    var messageCallback = GetAsyncCallback<(RedisChannel channel, string bs)>(data =>
                    {
                        if (data.channel.Equals(_journalHelper.GetEventsChannel()))
                        {
                            Log.Debug("Message received");

                            switch (_state)
                            {
                                case State.Idle:
                                    // do nothing, no query is running and no client request was performed
                                    break;
                                case State.Querying:
                                    _state = State.NotifiedWhenQuerying;
                                    break;
                                case State.NotifiedWhenQuerying:
                                    // do nothing we already know that some new events may exist
                                    break;
                                case State.WaitingForNotification:
                                    _state = State.Idle;
                                    Query();
                                    break;
                                default:
                                    Log.Error($"Unexpected source state: {_state}");
                                    FailStage(new IllegalStateException($"Unexpected source state: {_state}"));
                                    break;
                            }
                        }
                        else
                        {
                            Log.Debug($"Message from unexpected channel: {data.channel}");
                        }
                    });

                    _subscription = _redis.GetSubscriber();
                    _subscription.Subscribe(_journalHelper.GetEventsChannel(), (channel, value) =>
                    {
                        messageCallback.Invoke((channel, value));
                    });
                }
                else
                {
                    // start by first querying the current length of tag events
                    // for the given tag
                    // stream will stop once this has been delivered
                    _state = State.Initializing;

                    var initCallback = GetAsyncCallback<long>(len =>
                    {
                        _maxOffset = len;
                        switch (_state)
                        {
                            case State.QueryWhenInitializing:
                                // during initialization, downstream asked for an element,
                                // let’s query elements
                                _state = State.Idle;
                                Query();
                                break;
                            case State.Initializing:
                                // no request from downstream, just go idle
                                _state = State.Idle;
                                break;
                            default:
                                Log.Error($"Unexpected source state when initializing: {_state}");
                                FailStage(new IllegalStateException($"Unexpected source state when initializing: {_state}"));
                                break;
                        }
                    });

                    _redis.GetDatabase(_database).ListLengthAsync(_journalHelper.GetEventsKey()).ContinueWith(task =>
                    {
                        if (!task.IsCanceled || task.IsFaulted)
                        {
                            initCallback(task.Result - 1);
                        }
                        else
                        {
                            Log.Error(task.Exception, "Error while initializing current events by tag");
                            FailStage(task.Exception);
                        }
                    });
                }
            }

            public override void PostStop()
            {
                _subscription?.UnsubscribeAll();
            }

            private void Query()
            {
                switch (_state)
                {
                    case State.Idle:
                        if (_buffer.Count == 0)
                        {
                            // so, we need to fill this buffer
                            _state = State.Querying;

                            // request next batch of events for this tag (potentially limiting to the max offset in the case of non live stream)
                            var refs = _redis.GetDatabase(_database).ListRange(_journalHelper.GetEventsKey(), _currentOffset, Math.Min(_maxOffset, _currentOffset + _max - 1));

                            var trans = _redis.GetDatabase(_database).CreateTransaction();

                            var events = refs.Select(bytes =>
                            {
                                var (sequenceNr, persistenceId) = bytes.Deserialize();
                                return trans.SortedSetRangeByScoreAsync(_journalHelper.GetJournalKey(persistenceId), sequenceNr, sequenceNr);
                            }).ToList();

                            trans.ExecuteAsync().ContinueWith(task =>
                            {
                                if (!task.IsCanceled || task.IsFaulted)
                                {
                                    var callbackEvents = events.Select(bytes =>
                                    {
                                        var result = _journalHelper.PersistentFromBytes(bytes.Result.FirstOrDefault());
                                        return (result.PersistenceId, result);
                                    }).ToList();
                                    _callback((refs.Length, callbackEvents));
                                }
                                else
                                {
                                    Log.Error(task.Exception, "Error while querying events by persistence identifier");
                                    FailStage(task.Exception);
                                }
                            });
                        }
                        else
                        {
                            // buffer is non empty, let’s deliver buffered data
                            Deliver();
                        }
                        break;
                    default:
                        Log.Error($"Unexpected source state when querying: {_state}");
                        FailStage(new IllegalStateException($"Unexpected source state when querying: {_state}"));
                        break;
                }
            }

            private void Deliver()
            {
                // go back to idle state, waiting for more client request
                _state = State.Idle;
                var elem = _buffer.Dequeue();
                Push(_outlet, elem);
                if (_buffer.Count == 0 && _currentOffset >= _maxOffset)
                {
                    // max offset has been reached and delivered, complete
                    CompleteStage();
                }
            }
        }
    }

}
