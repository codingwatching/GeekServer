﻿using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using MongoDB.Driver;
using MongoDB.Bson;

namespace Geek.Server
{
    public sealed class StateComponent
    {
        static readonly NLog.Logger LOGGER = NLog.LogManager.GetCurrentClassLogger();
        static readonly object lockObj = new object();
        static List<Func<Task>> shutdownFunc = new List<Func<Task>>();
        static readonly ConcurrentQueue<Func<Task>> timerFuncList = new ConcurrentQueue<Func<Task>>();
        public static void AddShutdownSaveFunc(Func<Task> shutdown, Func<Task> timer)
        {
            lock (lockObj)
            {
                shutdownFunc.Add(shutdown);
                if (!timerFuncList.Contains(timer))
                    timerFuncList.Enqueue(timer);
            }
        }

        /// <summary>
        /// 停服时回存所有数据
        /// </summary>
        public static async Task SaveAllState()
        {
            try
            {
                var start = DateTime.Now;
                var tasks = new List<Task>();
                foreach (var item in shutdownFunc)
                {
                    tasks.Add(item());
                }
                await Task.WhenAll(tasks);
                LOGGER.Info("all state save time:{}毫秒", (DateTime.Now - start).TotalMilliseconds);
            }
            catch (Exception e)
            {
                LOGGER.Info("save all state error");
                LOGGER.Error(e.ToString());
            }
        }

        /// <summary>
        /// 定时回存所有数据
        /// </summary>
        public static async Task TimerSave()
        {
            try
            {
                foreach (var func in timerFuncList)
                {
                    await func();
                    if (!GlobalDBTimer.Singleton.Working)
                        return;
                }
            }
            catch (Exception e)
            {
                LOGGER.Info("timer save state error");
                LOGGER.Error(e.ToString());
            }
        }
    }


    /// <summary>
    /// 所有需要存储数据的系统由此派生
    /// </summary>
    public abstract class StateComponent<TState> : BaseComponent, IState where TState : DBState, new()
    {
        static readonly NLog.Logger LOGGER = NLog.LogManager.GetCurrentClassLogger();
        public T GetActor<T>() where T : ComponentActor { return (T)Actor; }

        protected TState _State;
        public DBState State => _State;
        
        public override async Task Active()
        {
            if (IsActive)
                return;

            await ReadStateAsync();
            aTypeAllStateMap.TryRemove(_State.Id, out _);
            aTypeAllStateMap.TryAdd(_State.Id, _State);
        }

        static StateComponent()
        {
            StateComponent.AddShutdownSaveFunc(saveAllStateOfAType, timerSaveAllStateOfAType);
        }

        static readonly ConcurrentDictionary<long, TState> aTypeAllStateMap = new ConcurrentDictionary<long, TState>();
        static async Task saveAllStateOfAType()
        {
            var type = typeof(TState);
            //批量回存当前类型的所有state
            var batchList = new List<WriteModel<TState>>();
            foreach (var kv in aTypeAllStateMap)
            {
                var state = kv.Value;
                var actor = await ActorManager.Get(state.Id);
                if (actor == null || actor.ReadOnly)
                    continue;

                //关服时直接调用，actor逻辑已停，安全
                if (!state.IsChanged)
                    continue;

                var model = MongoDBConnection.BuildUpdateOneModel(type, state, state.Id);
                batchList.Add(model);
            }

            var db = MongoDBConnection.Singleton.CurDateBase;
            var col = db.GetCollection<TState>(typeof(TState).FullName);
            int idx = 0;
            int once = 500;
            while (idx < batchList.Count)
            {
                var list = batchList.GetRange(idx, Math.Min(once, batchList.Count - idx));
                idx += once;
                bool saved = false;
                for (int i = 0; i < 3; ++i)
                {
                    var result = await col.BulkWriteAsync(list, new BulkWriteOptions() { IsOrdered = false });
                    if (result.IsAcknowledged)
                    {
                        saved = true;
                        break;
                    }
                    await Task.Delay(1000);
                }
                await Task.Delay(10);

                if (!saved)
                {
                    LOGGER.Error("存数据库失败，可以先存到磁盘");
                }
            }
        }

        static async Task timerSaveAllStateOfAType()
        {
            var type = typeof(TState);

            //批量回存当前类型的所有state
            var taskList = new List<Task>();
            var changedStateIdEuque = new ConcurrentQueue<long>();
            var batchQueue = new ConcurrentQueue<WriteModel<TState>>();
            foreach (var kv in aTypeAllStateMap)
            {
                var state = kv.Value;
                var actor = await ActorManager.Get(state.Id);
                if (actor == null || actor.ReadOnly)
                    continue;

                var filter = Builders<BsonDocument>.Filter.Eq(MongoField.Id, state.Id);
                var task = actor.SendAsync(() => {
                    if (!state.IsChanged)
                        return;

                    var model = MongoDBConnection.BuildUpdateOneModel(type, state, state.Id);
                    batchQueue.Enqueue(model);
                    changedStateIdEuque.Enqueue(state.Id);

                });
                taskList.Add(task);
            }
            await Task.WhenAll(taskList);

            var batchList = new List<WriteModel<TState>>();
            batchList.AddRange(batchQueue);
            var changedStateIdList = new List<long>();
            changedStateIdList.AddRange(changedStateIdEuque);
            var db = MongoDBConnection.Singleton.CurDateBase;
            var col = db.GetCollection<TState>(typeof(TState).FullName);
            int idx = 0;
            int once = 500;
            while (idx < batchList.Count)
            {
                var list = batchList.GetRange(idx, Math.Min(once, batchList.Count - idx));
                var stateIdList = changedStateIdList.GetRange(idx, list.Count);
                idx += once;
                var ret = await col.BulkWriteAsync(list, new BulkWriteOptions() { IsOrdered = false });
                if (ret.IsAcknowledged)
                {
                    foreach (var id in stateIdList)
                    {
                        aTypeAllStateMap.TryGetValue(id, out var state);
                        if (state == null)
                            continue;
                        var actor = await ActorManager.Get(state.Id);
                        _ = actor.SendAsync(state.ClearChanges, false);
                    }
                }
                await Task.Delay(100);
            }
        }

        long stateLoadedTime;
        /// <summary>其他服的玩家每次获取State数据时调用</summary>
        public async Task ReloadState(int coldTimeInMinutes = 30)
        {
            if ((DateTime.Now - new DateTime(stateLoadedTime)).TotalMinutes < coldTimeInMinutes)
                return;

            stateLoadedTime = DateTime.Now.Ticks;
            _State = await MongoDBConnection.Singleton.LoadState<TState>(ActorId);
        }

        public async Task ReadStateAsync()
        {
            stateLoadedTime = DateTime.Now.Ticks;
            _State = await MongoDBConnection.Singleton.LoadState<TState>(ActorId);
            IsActive = true;
        }

        public Task WriteStateAsync()
        {
            return MongoDBConnection.Singleton.SaveState(_State);
        }

        /// <summary>
        /// 这里不回存，仍然依赖定时回存去回存
        /// </summary>
        public override Task Deactive()
        {
            aTypeAllStateMap.TryRemove(_State.Id, out _);
            return base.Deactive();
        }

        //是不是可以回收了
        internal override Task<bool> ReadyToDeactive()
        {
            if (_State == null)
                return Task.FromResult(true);
            return Task.FromResult(!_State.IsChanged);
        }
    }
}