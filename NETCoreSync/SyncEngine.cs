﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Linq.Expressions;
using System.Linq.Dynamic.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NETCoreSync.Exceptions;
using System.Reflection;
using System.IO;
using System.IO.Compression;

namespace NETCoreSync
{
    public abstract partial class SyncEngine
    {
        internal readonly SyncConfiguration SyncConfiguration;

        public SyncEngine(SyncConfiguration syncConfiguration)
        {
            SyncConfiguration = syncConfiguration ?? throw new NullReferenceException(nameof(syncConfiguration));
        }

        internal void GetChanges(GetChangesParameter parameter, ref GetChangesResult result)
        {
            if (parameter == null) throw new NullReferenceException(nameof(parameter));
            result = new GetChangesResult(parameter.PayloadAction, parameter.SynchronizationId, parameter.CustomInfo);
            result.MaxTimeStamp = GetMinValueTicks();
            if (parameter.LastSync == 0) parameter.LastSync = GetMinValueTicks();

            parameter.Log.Add($"Preparing Data Since LastSync: {parameter.LastSync}");
            parameter.Log.Add($"SyncTypes Count: {SyncConfiguration.SyncTypes.Count}");
            for (int i = 0; i < SyncConfiguration.SyncTypes.Count; i++)
            {
                Type syncType = SyncConfiguration.SyncTypes[i];
                parameter.Log.Add($"Processing Type: {syncType.Name} ({i + 1} of {SyncConfiguration.SyncTypes.Count})");
                parameter.Log.Add($"Getting Type Changes...");
                List<object> appliedIds = null;
                if (parameter.AppliedIds != null && parameter.AppliedIds.ContainsKey(syncType)) appliedIds = parameter.AppliedIds[syncType];
                (JObject typeChanges, int typeChangesCount, long typeMaxTimeStamp, List<SyncLog.SyncLogData> typeLogChanges) = GetTypeChanges(parameter.LastSync, syncType, parameter.SynchronizationId, parameter.CustomInfo, appliedIds);
                parameter.Log.Add($"Type Changes Count: {typeChangesCount}");
                if (typeChangesCount != 0 && typeChanges != null) result.Changes.Add(typeChanges);
                if (typeMaxTimeStamp > result.MaxTimeStamp) result.MaxTimeStamp = typeMaxTimeStamp;
                result.LogChanges.AddRange(typeLogChanges);
                parameter.Log.Add($"Type: {syncType.Name} Processed");
            }
        }

        internal (JObject typeChanges, int typeChangesCount, long maxTimeStamp, List<SyncLog.SyncLogData> logChanges) GetTypeChanges(long? lastSync, Type syncType, string synchronizationId, Dictionary<string, object> customInfo, List<object> appliedIds)
        {
            if (string.IsNullOrEmpty(synchronizationId)) throw new NullReferenceException(nameof(synchronizationId));
            if (lastSync == null) lastSync = GetMinValueTicks();
            if (customInfo == null) customInfo = new Dictionary<string, object>();
            long maxTimeStamp = GetMinValueTicks();
            List<SyncLog.SyncLogData> logChanges = new List<SyncLog.SyncLogData>();
            SyncConfiguration.SchemaInfo schemaInfo = GetSchemaInfo(SyncConfiguration, syncType);
            JObject typeChanges = null;
            JArray datas = new JArray();

            OperationType operationType = OperationType.GetChanges;
            object transaction = StartTransaction(syncType, operationType, synchronizationId, customInfo);
            try
            {
                IQueryable queryable = InvokeGetQueryable(syncType, transaction, operationType, synchronizationId, customInfo);
                if (appliedIds == null || appliedIds.Count == 0)
                {
                    queryable = queryable.Where($"{schemaInfo.PropertyInfoLastUpdated.Name} > @0 || ({schemaInfo.PropertyInfoDeleted.Name} != null && {schemaInfo.PropertyInfoDeleted.Name} > @1)", lastSync.Value, lastSync);
                }
                else
                {
                    Type typeId = Type.GetType(schemaInfo.PropertyInfoId.PropertyType, true);
                    MethodInfo miCast = typeof(Enumerable).GetMethod("Cast").MakeGenericMethod(typeId);
                    object appliedIdsTypeId = miCast.Invoke(appliedIds, new object[] { appliedIds });
                    queryable = queryable.Where($"({schemaInfo.PropertyInfoLastUpdated.Name} > @0 || ({schemaInfo.PropertyInfoDeleted.Name} != null && {schemaInfo.PropertyInfoDeleted.Name} > @1)) && !(@2.Contains({schemaInfo.PropertyInfoId.Name}))", lastSync.Value, lastSync, appliedIdsTypeId);
                }
                queryable = queryable.OrderBy($"{schemaInfo.PropertyInfoDeleted.Name}, {schemaInfo.PropertyInfoLastUpdated.Name}");
                List<dynamic> dynamicDatas = queryable.ToDynamicList();
                if (dynamicDatas.Count > 0)
                {
                    typeChanges = new JObject();
                    typeChanges[nameof(syncType)] = syncType.Name;
                    typeChanges[nameof(schemaInfo)] = schemaInfo.ToJObject();
                    foreach (dynamic dynamicData in dynamicDatas)
                    {
                        JObject jObjectData = InvokeSerializeDataToJson(syncType, dynamicData, schemaInfo, transaction, operationType, synchronizationId, customInfo);
                        datas.Add(jObjectData);
                        long lastUpdated = jObjectData[schemaInfo.PropertyInfoLastUpdated.Name].Value<long>();
                        long? deleted = jObjectData[schemaInfo.PropertyInfoDeleted.Name].Value<long?>();
                        if (lastUpdated > maxTimeStamp) maxTimeStamp = lastUpdated;
                        if (deleted.HasValue && deleted.Value > maxTimeStamp) maxTimeStamp = deleted.Value;
                        logChanges.Add(SyncLog.SyncLogData.FromJObject(jObjectData, syncType, schemaInfo));
                    }
                    typeChanges[nameof(datas)] = datas;
                }
                CommitTransaction(syncType, transaction, operationType, synchronizationId, customInfo);
            }
            catch (Exception)
            {
                RollbackTransaction(syncType, transaction, operationType, synchronizationId, customInfo);
                throw;
            }
            finally
            {
                EndTransaction(syncType, transaction, operationType, synchronizationId, customInfo);
            }
            return (typeChanges, datas.Count, maxTimeStamp, logChanges);
        }

        internal void ApplyChanges(ApplyChangesParameter parameter, ref ApplyChangesResult result)
        {
            if (parameter == null) throw new NullReferenceException(nameof(parameter));
            result = new ApplyChangesResult(parameter.PayloadAction, parameter.SynchronizationId, parameter.CustomInfo);
            
            parameter.Log.Add("Applying Data Type Changes...");
            parameter.Log.Add($"SyncTypes Count: {parameter.Changes.Count}");
            List<Type> postEventTypes = new List<Type>();
            Dictionary<Type, List<object>> dictDeletedIds = new Dictionary<Type, List<object>>();
            for (int i = 0; i < parameter.Changes.Count; i++)
            {
                JObject typeChanges = parameter.Changes[i].Value<JObject>();
                parameter.Log.Add($"Applying Type: {typeChanges["syncType"].Value<string>()}...");
                (Type localSyncType, List<object> appliedIds, List<object> deletedIds) = ApplyTypeChanges(parameter.Log, result.Inserts, result.Updates, result.Deletes, result.Conflicts, typeChanges, parameter.SynchronizationId, parameter.CustomInfo);
                parameter.Log.Add($"Type: {typeChanges["syncType"].Value<string>()} Applied, Count: {appliedIds.Count}");
                result.AppliedIds[localSyncType] = appliedIds;
                if (deletedIds.Count > 0)
                {
                    if (!postEventTypes.Contains(localSyncType)) postEventTypes.Add(localSyncType);
                    dictDeletedIds[localSyncType] = deletedIds;
                }
            }
            if (postEventTypes.Count > 0)
            {
                parameter.Log.Add("Processing Post Events...");
                parameter.Log.Add($"Post Event Types Count: {postEventTypes.Count}");
                for (int i = 0; i < postEventTypes.Count; i++)
                {
                    Type postEventType = postEventTypes[i];
                    if (dictDeletedIds.ContainsKey(postEventType))
                    {
                        parameter.Log.Add($"Processing Post Event Delete for Type: {postEventType.Name}, Count: {dictDeletedIds[postEventType].Count}");
                        for (int j = 0; j < dictDeletedIds[postEventType].Count; j++)
                        {
                            PostEventDelete(postEventType, dictDeletedIds[postEventType][j], parameter.SynchronizationId, parameter.CustomInfo);
                        }
                    }
                }
            }
        }

        internal (Type localSyncType, List<object> appliedIds, List<object> deletedIds) ApplyTypeChanges(List<string> log, List<SyncLog.SyncLogData> inserts, List<SyncLog.SyncLogData> updates, List<SyncLog.SyncLogData> deletes, List<SyncLog.SyncLogConflict> conflicts, JObject typeChanges, string synchronizationId, Dictionary<string, object> customInfo)
        {
            List<object> appliedIds = new List<object>();
            List<object> deletedIds = new List<object>();
            if (inserts == null) inserts = new List<SyncLog.SyncLogData>();
            if (updates == null) updates = new List<SyncLog.SyncLogData>();
            if (deletes == null) deletes = new List<SyncLog.SyncLogData>();
            if (conflicts == null) conflicts = new List<SyncLog.SyncLogConflict>();
            string syncTypeName = typeChanges["syncType"].Value<string>();
            JObject jObjectSchemaInfo = typeChanges["schemaInfo"].Value<JObject>();
            SyncConfiguration.SchemaInfo schemaInfo = SyncConfiguration.SchemaInfo.FromJObject(jObjectSchemaInfo);
            string localSyncTypeName = syncTypeName;
            if (!string.IsNullOrEmpty(schemaInfo.SyncSchemaAttribute.MapToClassName)) localSyncTypeName = schemaInfo.SyncSchemaAttribute.MapToClassName;
            Type localSyncType = SyncConfiguration.SyncTypes.Where(w => w.Name == localSyncTypeName).FirstOrDefault();
            if (localSyncType == null) throw new SyncEngineConstraintException($"Unable to find SyncType: {localSyncTypeName} in SyncConfiguration");
            SyncConfiguration.SchemaInfo localSchemaInfo = GetSchemaInfo(SyncConfiguration, localSyncType);

            OperationType operationType = OperationType.ApplyChanges;
            object transaction = StartTransaction(localSyncType, operationType, synchronizationId, customInfo);
            try
            {
                IQueryable queryable = InvokeGetQueryable(localSyncType, transaction, operationType, synchronizationId, customInfo);
                JArray datas = typeChanges["datas"].Value<JArray>();
                log.Add($"Data Count: {datas.Count}");
                for (int i = 0; i < datas.Count; i++)
                {
                    JObject jObjectData = datas[i].Value<JObject>();
                    JValue id = jObjectData[schemaInfo.PropertyInfoId.Name].Value<JValue>();
                    long lastUpdated = jObjectData[schemaInfo.PropertyInfoLastUpdated.Name].Value<long>();
                    long? deleted = jObjectData[schemaInfo.PropertyInfoDeleted.Name].Value<long?>();

                    object localId = TransformIdType(localSyncType, id, transaction, operationType, synchronizationId, customInfo);
                    dynamic dynamicData = queryable.Where($"{localSchemaInfo.PropertyInfoId.Name} == @0", localId).FirstOrDefault();
                    object localData = (object)dynamicData;
                    if (localData == null)
                    {
                        object newData = InvokeDeserializeJsonToNewData(localSyncType, jObjectData, transaction, operationType, synchronizationId, customInfo);
                        newData.GetType().GetProperty(localSchemaInfo.PropertyInfoId.Name).SetValue(newData, localId);
                        newData.GetType().GetProperty(localSchemaInfo.PropertyInfoLastUpdated.Name).SetValue(newData, lastUpdated);
                        newData.GetType().GetProperty(localSchemaInfo.PropertyInfoDeleted.Name).SetValue(newData, deleted);
                        PersistData(localSyncType, newData, true, transaction, operationType, synchronizationId, customInfo);
                        if (!appliedIds.Contains(localId)) appliedIds.Add(localId);
                        inserts.Add(SyncLog.SyncLogData.FromJObject(InvokeSerializeDataToJson(localSyncType, newData, localSchemaInfo, transaction, operationType, synchronizationId, customInfo), localSyncType, localSchemaInfo));
                    }
                    else
                    {
                        if (deleted == null)
                        {
                            long localLastUpdated = (long)localData.GetType().GetProperty(localSchemaInfo.PropertyInfoLastUpdated.Name).GetValue(localData);
                            if (lastUpdated > localLastUpdated)
                            {
                                object existingData = InvokeDeserializeJsonToExistingData(localSyncType, jObjectData, localData, localId, transaction, operationType, synchronizationId, customInfo, localSchemaInfo);
                                existingData.GetType().GetProperty(localSchemaInfo.PropertyInfoLastUpdated.Name).SetValue(existingData, lastUpdated);
                                PersistData(localSyncType, existingData, false, transaction, operationType, synchronizationId, customInfo);
                                if (!appliedIds.Contains(localId)) appliedIds.Add(localId);
                                updates.Add(SyncLog.SyncLogData.FromJObject(InvokeSerializeDataToJson(localSyncType, existingData, localSchemaInfo, transaction, operationType, synchronizationId, customInfo), localSyncType, localSchemaInfo));
                            }
                            else
                            {
                                log.Add($"CONFLICT Detected: Target Data is newer than Source Data. Id: {id}");
                                conflicts.Add(new SyncLog.SyncLogConflict(SyncLog.SyncLogConflict.ConflictTypeEnum.TargetDataIsNewerThanSource, SyncLog.SyncLogData.FromJObject(jObjectData, localSyncType, schemaInfo)));
                            }
                        }
                        else
                        {
                            object existingData = InvokeDeserializeJsonToExistingData(localSyncType, jObjectData, localData, localId, transaction, operationType, synchronizationId, customInfo, localSchemaInfo);
                            existingData.GetType().GetProperty(localSchemaInfo.PropertyInfoDeleted.Name).SetValue(existingData, deleted);
                            PersistData(localSyncType, existingData, false, transaction, operationType, synchronizationId, customInfo);
                            if (!appliedIds.Contains(localId)) appliedIds.Add(localId);
                            if (!deletedIds.Contains(localId)) deletedIds.Add(localId);
                            deletes.Add(SyncLog.SyncLogData.FromJObject(InvokeSerializeDataToJson(localSyncType, existingData, localSchemaInfo, transaction, operationType, synchronizationId, customInfo), localSyncType, localSchemaInfo));
                        }
                    }
                }
                CommitTransaction(localSyncType, transaction, operationType, synchronizationId, customInfo);
            }
            catch (Exception)
            {
                RollbackTransaction(localSyncType, transaction, operationType, synchronizationId, customInfo);
                throw;
            }
            finally
            {
                EndTransaction(localSyncType, transaction, operationType, synchronizationId, customInfo);
            }

            return (localSyncType, appliedIds, deletedIds);
        }

        internal void GetKnowledge(GetKnowledgeParameter parameter, ref GetKnowledgeResult result)
        {
            if (parameter == null) throw new NullReferenceException(nameof(parameter));
            result = new GetKnowledgeResult(parameter.PayloadAction, parameter.SynchronizationId, parameter.CustomInfo);

            result.KnowledgeInfos = GetAllKnowledgeInfos(parameter.SynchronizationId, parameter.CustomInfo);
            if (result.KnowledgeInfos == null) result.KnowledgeInfos = new List<KnowledgeInfo>();
            parameter.Log.Add($"All KnowledgeInfos Count: {result.KnowledgeInfos.Count}");
            if (result.KnowledgeInfos.Where(w => w.IsLocal).Count() > 1) throw new SyncEngineConstraintException("Found multiple KnowledgeInfo with IsLocal equals to true. IsLocal should be 1 (one) data only");
            if (result.KnowledgeInfos.Where(w => w.IsLocal).Count() == 1) return;

            parameter.Log.Add("Local KnowledgeInfo is not found. Creating a new Local KnowledgeInfo and Provisioning existing data...");
            OperationType operationType = OperationType.ProvisionKnowledge;
            object transaction = StartTransaction(null, operationType, parameter.SynchronizationId, parameter.CustomInfo);
            try
            {
                KnowledgeInfo localKnowledgeInfo = new KnowledgeInfo()
                {
                    DatabaseInstanceId = Guid.NewGuid().ToString(),
                    IsLocal = true
                };
                parameter.Log.Add("Getting Next TimeStamp...");
                long nextTimeStamp = InvokeGetNextTimeStamp();
                parameter.Log.Add("Provisioning All Existing Data with the acquired TimeStamp and DatabaseInstanceId...");
                parameter.Log.Add($"SyncTypes Count: {SyncConfiguration.SyncTypes.Count}");
                for (int i = 0; i < SyncConfiguration.SyncTypes.Count; i++)
                {
                    Type syncType = SyncConfiguration.SyncTypes[i];
                    parameter.Log.Add($"Processing Type: {syncType.Name} ({i + 1} of {SyncConfiguration.SyncTypes.Count})");
                    int dataCount = 0;
                    SyncConfiguration.SchemaInfo schemaInfo = GetSchemaInfo(SyncConfiguration, syncType);
                    IQueryable queryable = InvokeGetQueryable(syncType, transaction, operationType, parameter.SynchronizationId, parameter.CustomInfo);
                    System.Collections.IEnumerator enumerator = queryable.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        dataCount += 1;
                        object data = enumerator.Current;
                        data.GetType().GetProperty(schemaInfo.PropertyInfoDatabaseInstanceId.Name).SetValue(data, null);
                        data.GetType().GetProperty(schemaInfo.PropertyInfoLastUpdated.Name).SetValue(data, nextTimeStamp);
                        PersistData(syncType, data, false, transaction, operationType, parameter.SynchronizationId, parameter.CustomInfo);
                    }
                    parameter.Log.Add($"Type: {syncType.Name} Processed. Provisioned Data Count: {dataCount}");
                }
                localKnowledgeInfo.LastSyncTimeStamp = nextTimeStamp;
                parameter.Log.Add("Saving Local KnowledgeInfo...");
                CreateOrUpdateKnowledgeInfo(localKnowledgeInfo, parameter.SynchronizationId, parameter.CustomInfo);
                CommitTransaction(null, transaction, operationType, parameter.SynchronizationId, parameter.CustomInfo);
            }
            catch (Exception)
            {
                RollbackTransaction(null, transaction, operationType, parameter.SynchronizationId, parameter.CustomInfo);
                throw;
            }
            finally
            {
                EndTransaction(null, transaction, operationType, parameter.SynchronizationId, parameter.CustomInfo);
            }
            result.KnowledgeInfos = GetAllKnowledgeInfos(parameter.SynchronizationId, parameter.CustomInfo);
            if (result.KnowledgeInfos.Where(w => w.IsLocal).Count() != 1) throw new SyncEngineConstraintException($"KnowledgeInfo with IsLocal equals to true is still not 1 (one) data. Check your {nameof(CreateOrUpdateKnowledgeInfo)} implementation.");
        }

        internal void GetChangesByKnowledge(GetChangesByKnowledgeParameter parameter, ref GetChangesByKnowledgeResult result)
        {
            if (parameter == null) throw new NullReferenceException(nameof(parameter));
            result = new GetChangesByKnowledgeResult(parameter.PayloadAction, parameter.SynchronizationId, parameter.CustomInfo);

            parameter.Log.Add($"Get Changes By Knowledge");
            parameter.Log.Add($"Local Knowledge Count: {parameter.LocalKnowledgeInfos.Count}");
            for (int i = 0; i < parameter.LocalKnowledgeInfos.Count; i++)
            {
                parameter.Log.Add($"{i + 1}. {parameter.LocalKnowledgeInfos[i].ToString()}");
            }
            parameter.Log.Add($"Remote Knowledge Count: {parameter.RemoteKnowledgeInfos.Count}");
            for (int i = 0; i < parameter.RemoteKnowledgeInfos.Count; i++)
            {
                parameter.Log.Add($"{i + 1}. {parameter.RemoteKnowledgeInfos[i].ToString()}");
            }
            if (parameter.LocalKnowledgeInfos.Where(w => w.IsLocal).Count() != 1) throw new SyncEngineConstraintException($"{nameof(parameter.LocalKnowledgeInfos)} must have 1 IsLocal property equals to true");
            if (parameter.RemoteKnowledgeInfos.Where(w => w.IsLocal).Count() != 1) throw new SyncEngineConstraintException($"{nameof(parameter.RemoteKnowledgeInfos)} must have 1 IsLocal property equals to true");
            KnowledgeInfo localInfo = parameter.LocalKnowledgeInfos.Where(w => w.IsLocal).First();
            long localMaxTimeStamp = 0;
            parameter.Log.Add($"SyncTypes Count: {SyncConfiguration.SyncTypes.Count}");
            for (int i = 0; i < SyncConfiguration.SyncTypes.Count; i++)
            {
                Type syncType = SyncConfiguration.SyncTypes[i];
                parameter.Log.Add($"Processing Type: {syncType.Name} ({i + 1} of {SyncConfiguration.SyncTypes.Count})");
                (JObject typeChanges, int typeChangesCount, List<SyncLog.SyncLogData> typeLogChanges, long currentLocalMaxTimeStamp) = GetTypeChangesByKnowledge(syncType, localInfo.DatabaseInstanceId, parameter.RemoteKnowledgeInfos, parameter.SynchronizationId, parameter.CustomInfo);
                parameter.Log.Add($"Type Changes Count: {typeChangesCount}");
                if (typeChangesCount != 0 && typeChanges != null) result.Changes.Add(typeChanges);
                result.LogChanges.AddRange(typeLogChanges);
                parameter.Log.Add($"Type: {syncType.Name} Processed");
                if (currentLocalMaxTimeStamp > localMaxTimeStamp) localMaxTimeStamp = currentLocalMaxTimeStamp;
            }
            if (localMaxTimeStamp > localInfo.LastSyncTimeStamp)
            {
                CreateOrUpdateKnowledgeInfo(localInfo, parameter.SynchronizationId, parameter.CustomInfo);
            }
        }

        internal (JObject typeChanges, int typeChangesCount, List<SyncLog.SyncLogData> logChanges, long localMaxTimeStamp) GetTypeChangesByKnowledge(Type syncType, string localDatabaseInstanceId, List<KnowledgeInfo> remoteKnowledgeInfos, string synchronizationId, Dictionary<string, object> customInfo)
        {
            if (string.IsNullOrEmpty(synchronizationId)) throw new NullReferenceException(nameof(synchronizationId));
            if (customInfo == null) customInfo = new Dictionary<string, object>();

            List<SyncLog.SyncLogData> logChanges = new List<SyncLog.SyncLogData>();
            long localMaxTimeStamp = 0;
            SyncConfiguration.SchemaInfo schemaInfo = GetSchemaInfo(SyncConfiguration, syncType);
            JObject typeChanges = null;
            JArray datas = new JArray();

            OperationType operationType = OperationType.GetChanges;
            object transaction = StartTransaction(syncType, operationType, synchronizationId, customInfo);
            try
            {
                IQueryable queryable = InvokeGetQueryable(syncType, transaction, operationType, synchronizationId, customInfo);

                string predicate = "";
                for (int i = 0; i < remoteKnowledgeInfos.Count; i++)
                {
                    KnowledgeInfo info = remoteKnowledgeInfos[i];
                    if (!string.IsNullOrEmpty(predicate)) predicate += " || ";
                    predicate += "(";
                    predicate += $"{schemaInfo.PropertyInfoDatabaseInstanceId.Name} = {(info.DatabaseInstanceId == localDatabaseInstanceId ? "null" : ("'" + info.DatabaseInstanceId + "'"))} ";
                    predicate += $" && {schemaInfo.PropertyInfoLastUpdated} > {info.LastSyncTimeStamp}";
                    predicate += ")";
                }
                List<string> remoteKnownDatabaseInstanceIds = remoteKnowledgeInfos.Select(s => s.DatabaseInstanceId).ToList();
                queryable = queryable.Where($"{predicate} || !(@0.Contains({schemaInfo.PropertyInfoDatabaseInstanceId.Name}))", remoteKnownDatabaseInstanceIds);
                queryable = queryable.OrderBy($"{schemaInfo.PropertyInfoDeleted.Name}, {schemaInfo.PropertyInfoLastUpdated.Name}");
                List<dynamic> dynamicDatas = queryable.ToDynamicList();
                if (dynamicDatas.Count > 0)
                {
                    typeChanges = new JObject();
                    typeChanges[nameof(syncType)] = syncType.Name;
                    typeChanges[nameof(schemaInfo)] = schemaInfo.ToJObject();
                    foreach (dynamic dynamicData in dynamicDatas)
                    {
                        JObject jObjectData = InvokeSerializeDataToJson(syncType, dynamicData, schemaInfo, transaction, operationType, synchronizationId, customInfo);
                        datas.Add(jObjectData);
                        string databaseInstanceId = jObjectData[schemaInfo.PropertyInfoDatabaseInstanceId.Name].Value<string>();
                        long lastUpdated = jObjectData[schemaInfo.PropertyInfoLastUpdated.Name].Value<long>();
                        if (string.IsNullOrEmpty(databaseInstanceId) && lastUpdated > localMaxTimeStamp) localMaxTimeStamp = lastUpdated;
                        logChanges.Add(SyncLog.SyncLogData.FromJObject(jObjectData, syncType, schemaInfo));
                    }
                    typeChanges[nameof(datas)] = datas;
                }
                CommitTransaction(syncType, transaction, operationType, synchronizationId, customInfo);
            }
            catch (Exception)
            {
                RollbackTransaction(syncType, transaction, operationType, synchronizationId, customInfo);
                throw;
            }
            finally
            {
                EndTransaction(syncType, transaction, operationType, synchronizationId, customInfo);
            }
            return (typeChanges, datas.Count, logChanges, localMaxTimeStamp);
        }

        private IQueryable InvokeGetQueryable(Type classType, object transaction, OperationType operationType, string synchronizationId, Dictionary<string, object> customInfo)
        {
            IQueryable queryable = GetQueryable(classType, transaction, operationType, synchronizationId, customInfo);
            if (queryable == null) throw new SyncEngineConstraintException($"{nameof(GetQueryable)} must not return null");
            if (queryable.ElementType.FullName != classType.FullName) throw new SyncEngineConstraintException($"{nameof(GetQueryable)} must return IQueryable with ElementType: {classType.FullName}");
            return queryable;
        }

        private JObject InvokeSerializeDataToJson(Type classType, object data, SyncConfiguration.SchemaInfo schemaInfo, object transaction, OperationType operationType, string synchronizationId, Dictionary<string, object> customInfo)
        {
            string json = SerializeDataToJson(classType, data, transaction, operationType, synchronizationId, customInfo);
            if (string.IsNullOrEmpty(json)) throw new SyncEngineConstraintException($"{nameof(SerializeDataToJson)} must not return null or empty string");
            JObject jObject = null;
            try
            {
                jObject = JsonConvert.DeserializeObject<JObject>(json);
            }
            catch (Exception e)
            {
                throw new SyncEngineConstraintException($"The returned value from {nameof(SerializeDataToJson)} cannot be parsed as JSON Object (JObject). Error: {e.Message}. Returned Value: {json}");
            }
            if (!jObject.ContainsKey(schemaInfo.PropertyInfoId.Name)) throw new SyncEngineConstraintException($"The parsed JSON Object (JObject) does not contain key: {schemaInfo.PropertyInfoId.Name} (SyncProperty Id)");
            if (!jObject.ContainsKey(schemaInfo.PropertyInfoLastUpdated.Name)) throw new SyncEngineConstraintException($"The parsed JSON Object (JObject) does not contain key: {schemaInfo.PropertyInfoLastUpdated.Name} (SyncProperty LastUpdated)");
            if (!jObject.ContainsKey(schemaInfo.PropertyInfoDeleted.Name)) throw new SyncEngineConstraintException($"The parsed JSON Object (JObject) does not contain key: {schemaInfo.PropertyInfoDeleted.Name} (SyncProperty Deleted)");
            return jObject;
        }

        private object InvokeDeserializeJsonToNewData(Type classType, JObject jObject, object transaction, OperationType operationType, string synchronizationId, Dictionary<string, object> customInfo)
        {
            object newData = DeserializeJsonToNewData(classType, jObject, transaction, operationType, synchronizationId, customInfo);
            if (newData == null) throw new SyncEngineConstraintException($"{nameof(DeserializeJsonToNewData)} must not return null");
            if (newData.GetType().FullName != classType.FullName) throw new SyncEngineConstraintException($"Expected returned Type: {classType.FullName} during {nameof(DeserializeJsonToNewData)}, but Type: {newData.GetType().FullName} is returned instead.");
            return newData;
        }

        private object InvokeDeserializeJsonToExistingData(Type classType, JObject jObject, object data, object localId, object transaction, OperationType operationType, string synchronizationId, Dictionary<string, object> customInfo, SyncConfiguration.SchemaInfo localSchemaInfo)
        {
            object existingData = DeserializeJsonToExistingData(classType, jObject, data, transaction, operationType, synchronizationId, customInfo);
            if (existingData == null) throw new SyncEngineConstraintException($"{nameof(DeserializeJsonToExistingData)} must not return null");
            if (existingData.GetType().FullName != classType.FullName) throw new SyncEngineConstraintException($"Expected returned Type: {classType.FullName} during {nameof(DeserializeJsonToExistingData)}, but Type: {existingData.GetType().FullName} is returned instead.");
            object existingDataId = classType.GetProperty(localSchemaInfo.PropertyInfoId.Name).GetValue(existingData);
            if (!existingDataId.Equals(localId)) throw new SyncEngineConstraintException($"The returned Object Id ({existingDataId}) is different than the existing data Id: {localId}");
            return existingData;
        }

        internal long InvokeGetClientLastSync()
        {
            long lastSync = GetClientLastSync();
            long minValueTicks = GetMinValueTicks();
            if (lastSync < minValueTicks) lastSync = minValueTicks;
            return lastSync;
        }

        internal long InvokeGetNextTimeStamp()
        {
            long nextTimeStamp = GetNextTimeStamp();
            if (nextTimeStamp == 0) throw new SyncEngineConstraintException("GetNextTimeStamp should return value greater than zero");
            return nextTimeStamp;
        }
    }
}
