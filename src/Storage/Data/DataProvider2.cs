﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SenseNet.ContentRepository.Search.Indexing;
using SenseNet.ContentRepository.Storage.DataModel;
using SenseNet.ContentRepository.Storage.Schema;
using SenseNet.Diagnostics;
using SenseNet.Search.Indexing;

// ReSharper disable once CheckNamespace
namespace SenseNet.ContentRepository.Storage.Data
{
    /// <summary>
    /// ... Recommended minimal object structure: Nodes -> Versions --> BinaryProperties -> Files
    ///                                                         |-> LongTextProperties
    /// ... Additional structure: TreeLocks, LogEntries, IndexingActivities
    /// </summary>
    public abstract class DataProvider2
    {
        /// <summary>
        /// ... (MSSQL: unique index size is 900 byte)
        /// </summary>
        public virtual int PathMaxLength { get; } = 450;
        public virtual DateTime DateTimeMinValue { get; } = DateTime.MinValue;
        public virtual DateTime DateTimeMaxValue { get; } = DateTime.MaxValue;
        public virtual decimal DecimalMinValue { get; } = decimal.MinValue;
        public virtual decimal DecimalMaxValue { get; } = decimal.MinValue;

        public virtual void Reset()
        {
            // Do nothing if the provider is stateless.
        }

        /* =============================================================================================== Extensions */

        private readonly Dictionary<Type, IDataProviderExtension> _dataProvidersByType = new Dictionary<Type, IDataProviderExtension>();

        public virtual void SetExtension(Type providerType, IDataProviderExtension provider)
        {
            _dataProvidersByType[providerType] = provider;
        }

        internal T GetExtensionInstance<T>() where T : class, IDataProviderExtension
        {
            if (_dataProvidersByType.TryGetValue(typeof(T), out var provider))
                return provider as T;
            return null;
        }

        /* =============================================================================================== Nodes */

        /// <summary>
        /// Persists a brand new objects that contains all static and dynamic properties of the actual node (see the algorithm).
        /// Writes back the newly generated data to the given [nodeHeadData], [versionData] and [dynamicData] parameters:
        ///     NodeId, NodeTimestamp, VersionId, VersionTimestamp, BinaryPropertyIds, LastMajorVersionId, LastMinorVersionId.
        /// ... Need to be transactional
        /// ... Algorithm:
        ///  1 - Begin a new transaction
        ///  2 - Check the [nodeHeadData].Path uniqueness. If not, throw NodeAlreadyExistsException.
        ///  3 - Ensure the new unique NodeId and use it in the node head representation.
        ///  4 - Ensure the new unique VersionId and use it in the version head representation and any other version related data.
        ///  5 - Store (insert) the [versionData] representation.
        ///  6 - Ensure that the timestamp of the stored version is incremented.
        ///  7 - Store (insert) all representation of the dynamic property data including long texts, binary properties and files.
        ///      Use the new versionId in these items. It is strongly recommended that BinaryProperties and files be managed with
        ///      the BlobStorage API (e.g. BlobStorage.InsertBinaryProperty method).
        ///  8 - Collect last versionIds (last major and last minor).
        ///  9 - Store (insert) the [nodeHeadData] reresentation. Use the last major and minor versionIds.
        /// 10 - Ensure that the timestamp of the stored nodeHead is incremented and write back this value to the [nodeHeadData].Timestamp.
        /// 11 - Write back the following changed values:
        ///      - new nodeId: [nodeHeadData].NodeId
        ///      - new versionId: [versionData].VersionId
        ///      - nodeHead timestamp: [nodeHeadData].Timestamp
        ///      - version timestamp: [versionData].Timestamp
        ///      - last major version id: [nodeHeadData].LastMajorVersionId
        ///      - last minor version id: [nodeHeadData].LastMinorVersionId
        ///      - Update all changed Id and FileId of the BinaryDataValue in the [dynamicData].BinaryProperties if the
        ///        BinaryProperties or files are not managed with the BlobStorage API.
        /// 12 - Commit the transaction. If there is any problem, rollback the transaction and throw/rethrow an exception.
        ///      In case of error the written back data (new ids and changed timestamps)
        ///      will be dropped so rollback these data is not necessary.
        /// </summary>
        /// <param name="nodeHeadData">Head data of the node. Contains identical information, place in the Big-tree and the most important
        /// not-versioned property values.</param>
        /// <param name="versionData">Head information of the current version.</param>
        /// <param name="dynamicData">Metadata and blob data of the current version. Separated to some sub collections:
        /// BinaryProperties: Contain blob information (stream and metadata)
        /// LongTextProperties: Contain long textual values that can be lazy loaded.
        /// DynamicProperties: All dynamic property values except the binaries and long texts.
        /// </param>
        /// <returns>A Task that represents the asynchronous operation.</returns>
        public abstract Task InsertNodeAsync(NodeHeadData nodeHeadData, VersionData versionData, DynamicPropertyData dynamicData);

        /// <summary>
        /// Updates all objects that contains all static and dynamic properties of the actual node (see the algorithm).
        /// Updates the paths in the subtree if the node is renamed (i.e. Name property changed).
        /// Writes back the newly generated data to the given [nodeHeadData], [versionData] and [dynamicData] parameters:
        ///     NodeTimestamp, VersionTimestamp, BinaryPropertyIds, LastMajorVersionId, LastMinorVersionId.
        /// ... Need to be transactional
        /// ... Algorithm:
        ///  1 - Begin a new transaction
        ///  2 - Check the node existence by [nodeHeadData].NodeId. Throw an ____ exception if the node is deleted.
        ///  3 - Check the version existence by [versionData].VersionId. Throw an ____ exception if the version is deleted.
        ///  4 - Check the concurrent update. If the [nodeHeadData].Timestap and stored not timestamp are not equal, throw a NodeIsOutOfDateException
        ///  5 - Update the stored version head data implementation by the [versionData].VersionId with the [versionData].
        ///  6 - Ensure that the timestamp of the stored version is incremented.
        ///  7 - Delete unnecessary version representations by the given [versionIdsToDelete]
        ///  8 - Update all representation of the dynamic property data including long texts, binary properties and files.
        ///      Use the new versionId in these items. It is strongly recommended that BinaryProperties and files be managed with
        ///      the BlobStorage API (e.g. BlobStorage.UpdateBinaryProperty method).
        ///  9 - Collect last versionIds (last major and last minor).
        /// 10 - Update the [nodeHeadData] reresentation. Use the last major and minor versionIds.
        /// 11 - Ensure that the timestamp of the stored nodeHead is incremented.
        /// 12 - Update paths in the subtree if the [originalPath] is not null. For example: if the [originalPath] is "/Root/Folder1",
        ///      1 - All path will be changed if it starts with "/Root/Folder1/" ([originalPath] + trailing slash, case insensitive).
        ///      2 - Replace the [original path] to the new path in the [nodeHeadData].Path.
        /// 13 - Write back the following changed values:
        ///      - new versionId: [versionData].VersionId
        ///      - nodeHead timestamp: [nodeHeadData].Timestamp
        ///      - version timestamp: [versionData].Timestamp
        ///      - last major version id: [nodeHeadData].LastMajorVersionId
        ///      - last minor version id: [nodeHeadData].LastMinorVersionId
        ///      - Update all changed Id and FileId of the BinaryDataValue in the [dynamicData].BinaryProperties if the
        ///        BinaryProperties or files are not managed with the BlobStorage API.
        /// 14 - Commit the transaction. If there is any problem, rollback the transaction and throw/rethrow an exception.
        ///      In case of error the written back data (new ids and changed timestamps)
        ///      will be dropped so rollback these data is not necessary.
        /// </summary>
        /// <param name="nodeHeadData">Head data of the node. Contains identical information, place in the Big-tree and the most important
        /// not-versioned property values.</param>
        /// <param name="versionData">Head information of the current version.</param>
        /// <param name="dynamicData">Metadata and blob data of the current version. Separated to some sub collections:
        /// BinaryProperties: Contain blob information (stream and metadata)
        /// LongTextProperties: Contain long textual values that can be lazy loaded.
        /// DynamicProperties: All dynamic property values except the binaries and long texts.
        /// </param>
        /// <param name="versionIdsToDelete">Set of versionIds that defines the versions that need to be deleted. Can be empty but never null.</param>
        /// <param name="originalPath">Contains the node's original path if it is renamed. Null if the name was not changed.</param>
        /// <returns>A Task that represents the asynchronous operation.</returns>
        public abstract Task UpdateNodeAsync(
            NodeHeadData nodeHeadData, VersionData versionData, DynamicPropertyData dynamicData, IEnumerable<int> versionIdsToDelete,
            string originalPath = null);

        /// <summary>
        /// Copies all objects that contains all static and dynamic properties of the actual node (except the nodeHead representation)
        /// and updates the copy with the given data. Source version is identified by the [versionData].VersionId. Updates the paths
        /// in the subtree if the node is renamed (i.e. Name property changed). Target version descriptor is the [expectedVersionId]
        /// parameter. See the algorithm below.
        /// Writes back the newly generated data to the given [nodeHeadData], [versionData] and [dynamicData] parameters:
        ///     NodeTimestamp, VersionId, VersionTimestamp, BinaryPropertyIds, LastMajorVersionId, LastMinorVersionId.
        /// ... Need to be transactional
        ///  1 - Begin a new transaction
        ///  2 - Check the node existence by [nodeHeadData].NodeId. Throw an ____ exception if the node is deleted.
        ///  3 - Check the version existence by [versionData].VersionId. Throw an ____ exception if the version is deleted.
        ///  4 - Check the concurrent update. If the [nodeHeadData].Timestap and stored not timestamp are not equal, throw a NodeIsOutOfDateException
        ///  5 - Determine the target version: if [expectedVersionId] is not null, load the existing by the version head representation 
        ///      by the [expectedVersionId] otherwise create a brand new one.
        ///  6 - Copy the source version head data to the target representation and update with the [versionData].
        ///  7 - Ensure that the timestamp of the stored version is incremented.
        ///  8 - Copy the dynamic data representation by source versionId to the target representation and update with the
        ///      [dynamicData].DynamicProperties
        ///  9 - Copy the longText data representation by source versionId to the target representation and update with the
        ///      [dynamicData].LongTextProperties
        /// 10 - Save binary properties to the target version (copy old values is unnecessary because all binary properties were loaded before save).
        ///      It is strongly recommended that BinaryProperties and files be managed with the BlobStorage API (e.g. BlobStorage.InsertBinaryProperty method).
        /// 11 - Delete unnecessary version representations by the given [versionIdsToDelete]
        /// 12 - Collect last versionIds (last major and last minor).
        /// 13 - Update the [nodeHeadData] reresentation. Use the last major and minor versionIds.
        /// 14 - Ensure that the timestamp of the stored nodeHead is incremented.
        /// 15 - Update paths in the subtree if the [originalPath] is not null. For example: if the [originalPath] is "/Root/Folder1",
        ///      1 - All path will be changed if it starts with "/Root/Folder1/" ([originalPath] + trailing slash, case insensitive).
        ///      2 - Replace the [original path] to the new path in the [nodeHeadData].Path.
        /// 16 - Write back the following changed values:
        ///      - new versionId: [versionData].VersionId
        ///      - nodeHead timestamp: [nodeHeadData].Timestamp
        ///      - version timestamp: [versionData].Timestamp
        ///      - last major version id: [nodeHeadData].LastMajorVersionId
        ///      - last minor version id: [nodeHeadData].LastMinorVersionId
        ///      - Update all changed Id and FileId of the BinaryDataValue in the [dynamicData].BinaryProperties if the
        ///        BinaryProperties or files are not managed with the BlobStorage API.
        /// 17 - Commit the transaction. If there is any problem, rollback the transaction and throw/rethrow an exception.
        ///      In case of error the written back data (new ids and changed timestamps)
        ///      will be dropped so rollback these data is not necessary.
        /// </summary>
        /// <param name="nodeHeadData">Head data of the node. Contains identical information, place in the Big-tree and the most important
        /// not-versioned property values.</param>
        /// <param name="versionData">Head information of the current version.</param>
        /// <param name="dynamicData">Metadata and blob data of the current version. Separated to some sub collections:
        /// BinaryProperties: Contain blob information (stream and metadata)
        /// LongTextProperties: Contain long textual values that can be lazy loaded.
        /// DynamicProperties: All dynamic property values except the binaries and long texts.
        /// </param>
        /// <param name="versionIdsToDelete">Set of versionIds that defines the versions that need to be deleted. Can be empty but never null.</param>
        /// <param name="expectedVersionId">Id of the target version. 0 means: need to create a new version.</param>
        /// <param name="originalPath">Contains the node's original path if it is renamed. Null if the name was not changed.</param>
        /// <returns>A Task that represents the asynchronous operation.</returns>
        public abstract Task CopyAndUpdateNodeAsync(
            NodeHeadData nodeHeadData, VersionData versionData, DynamicPropertyData dynamicData, IEnumerable<int> versionIdsToDelete,
            int expectedVersionId = 0, string originalPath = null);

        /// <summary>
        /// Updates the paths in the subtree if the node is renamed (i.e. Name property changed).
        /// ... Need to be transactional
        ///  1 - Begin a new transaction
        ///  2 - Check the node existence by [nodeHeadData].NodeId. Throw an ____ exception if the node is deleted.
        ///  3 - Check the concurrent update. If the [nodeHeadData].Timestap and stored not timestamp are not equal, throw a NodeIsOutOfDateException
        ///  4 - Delete unnecessary version representations by the given [versionIdsToDelete]
        ///  5 - Collect last versionIds (last major and last minor).
        ///  6 - Update the [nodeHeadData] reresentation. Use the last major and minor versionIds.
        ///  7 - Ensure that the timestamp of the stored nodeHead is incremented.
        ///  8 - Write back the following changed values:
        ///      - nodeHead timestamp: [nodeHeadData].Timestamp
        ///      - last major version id: [nodeHeadData].LastMajorVersionId
        ///      - last minor version id: [nodeHeadData].LastMinorVersionId
        ///  9 - Commit the transaction. If there is any problem, rollback the transaction and throw/rethrow an exception.
        ///      In case of error the written back data (new ids and changed timestamps)
        ///      will be dropped so rollback these data is not necessary.
        /// </summary>
        /// <param name="nodeHeadData"></param>
        /// <param name="versionIdsToDelete"></param>
        /// <returns>A Task that represents the asynchronous operation.</returns>
        public abstract Task UpdateNodeHeadAsync(NodeHeadData nodeHeadData, IEnumerable<int> versionIdsToDelete);

        /// <summary>
        /// Returns loaded NodeData by the given versionIds
        /// </summary>
        public abstract Task<IEnumerable<NodeData>> LoadNodesAsync(int[] versionIds);

        public abstract Task DeleteNodeAsync(NodeHeadData nodeHeadData);

        public abstract Task MoveNodeAsync(NodeHeadData sourceNodeHeadData, int targetNodeId, long targetTimestamp);

        public abstract Task<Dictionary<int, string>> LoadTextPropertyValuesAsync(int versionId, int[] notLoadedPropertyTypeIds);

        public abstract Task<BinaryDataValue> LoadBinaryPropertyValueAsync(int versionId, int propertyTypeId);

        public abstract Task<bool> NodeExistsAsync(string path);

        /* =============================================================================================== NodeHead */

        public abstract Task<NodeHead> LoadNodeHeadAsync(string path);
        public abstract Task<NodeHead> LoadNodeHeadAsync(int nodeId);
        public abstract Task<NodeHead> LoadNodeHeadByVersionIdAsync(int versionId);
        public abstract Task<IEnumerable<NodeHead>> LoadNodeHeadsAsync(IEnumerable<int> heads);
        public abstract Task<NodeHead.NodeVersion[]> GetNodeVersions(int nodeId);
        public abstract Task<IEnumerable<VersionNumber>> GetVersionNumbersAsync(int nodeId);
        public abstract Task<IEnumerable<VersionNumber>> GetVersionNumbersAsync(string path);

        /* =============================================================================================== NodeQuery */

        public abstract Task<int> InstanceCountAsync(int[] nodeTypeIds);
        public abstract Task<IEnumerable<int>> GetChildrenIdentfiersAsync(int parentId);
        public abstract Task<IEnumerable<int>> QueryNodesByTypeAndPathAndNameAsync(int[] nodeTypeIds, string[] pathStart, bool orderByPath, string name);
        public abstract Task<IEnumerable<int>> QueryNodesByTypeAndPathAndPropertyAsync(int[] nodeTypeIds, string pathStart, bool orderByPath, List<QueryPropertyData> properties);
        public abstract Task<IEnumerable<int>> QueryNodesByReferenceAndTypeAsync(string referenceName, int referredNodeId, int[] nodeTypeIds);

        /* =============================================================================================== Tree */

        public abstract Task<IEnumerable<NodeType>> LoadChildTypesToAllowAsync(int nodeId);
        public abstract Task<List<ContentListType>> GetContentListTypesInTreeAsync(string path);
        public abstract Task<IEnumerable<EntityTreeNodeData>> LoadEntityTreeAsync();

        /* =============================================================================================== TreeLock */

        public abstract Task<int> AcquireTreeLockAsync(string path);
        public abstract Task<bool> IsTreeLockedAsync(string path);
        public abstract Task ReleaseTreeLockAsync(int[] lockIds);
        public abstract Task<Dictionary<int, string>> LoadAllTreeLocksAsync();

        /* =============================================================================================== IndexDocument */

        public abstract Task SaveIndexDocumentAsync(NodeData nodeData, IndexDocument indexDoc);
        public abstract Task SaveIndexDocumentAsync(int versionId, IndexDocument indexDoc);

        public abstract Task<IEnumerable<IndexDocumentData>> LoadIndexDocumentsAsync(IEnumerable<int> versionIds);
        public abstract Task<IEnumerable<IndexDocumentData>> LoadIndexDocumentsAsync(string path, int[] excludedNodeTypes);

        public abstract Task<IEnumerable<int>> LoadNotIndexedNodeIdsAsync(int fromId, int toId);

        /* =============================================================================================== IndexingActivity */

        public abstract Task<int> GetLastIndexingActivityIdAsync();
        public abstract Task<IIndexingActivity[]> LoadIndexingActivitiesAsync(int fromId, int toId, int count, bool executingUnprocessedActivities, IIndexingActivityFactory activityFactory);
        public abstract Task<IIndexingActivity[]> LoadIndexingActivitiesAsync(int[] gaps, bool executingUnprocessedActivities, IIndexingActivityFactory activityFactory);
        public abstract Task<IIndexingActivity[]> LoadExecutableIndexingActivitiesAsync(IIndexingActivityFactory activityFactory, int maxCount, int runningTimeoutInSeconds);
        public abstract Task<ExecutableIndexingActivitiesResult> LoadExecutableIndexingActivitiesAsync(IIndexingActivityFactory activityFactory, int maxCount, int runningTimeoutInSeconds, int[] waitingActivityIds);
        public abstract Task RegisterIndexingActivityAsync(IIndexingActivity activity);
        public abstract Task UpdateIndexingActivityRunningStateAsync(int indexingActivityId, IndexingActivityRunningState runningState);
        public abstract Task RefreshIndexingActivityLockTimeAsync(int[] waitingIds);
        public abstract Task DeleteFinishedIndexingActivitiesAsync();
        public abstract Task DeleteAllIndexingActivitiesAsync();

        /* =============================================================================================== Schema */

        public abstract Task<RepositorySchemaData> LoadSchemaAsync();
        public abstract SchemaWriter CreateSchemaWriter();

        //UNDONE:DB ------Refactor: Move to SchemaWriter? Delete the freature and implement individually in the providers?
        /// <summary>
        /// Checks the given schemaTimestamp equality. If different, throws an error: Storage schema is out of date.
        /// Checks the schemaLock existence. If there is, throws an error
        /// otherwise create a SchemaLock and return its value.
        /// </summary>
        public abstract string StartSchemaUpdate_EXPERIMENTAL(long schemaTimestamp); // original: AssertSchemaTimestampAndWriteModificationDate(long timestamp);
        //UNDONE:DB ------Refactor: Move to SchemaWriter? Delete the freature and implement individually in the providers?
        /// <summary>
        /// Checks the given schemaLock equality. If different, throws an illegal operation error.
        /// Returns a newly generated schemaTimestamp.
        /// </summary>
        public abstract long FinishSchemaUpdate_EXPERIMENTAL(string schemaLock);

        /* =============================================================================================== Logging */

        public abstract Task WriteAuditEventAsync(AuditEventInfo auditEvent);

        /* =============================================================================================== Provider Tools */

        public abstract DateTime RoundDateTime(DateTime d);
        public abstract bool IsCacheableText(string text);
        public abstract Task<string> GetNameOfLastNodeWithNameBaseAsync(int parentId, string namebase, string extension);
        public abstract Task<long> GetTreeSizeAsync(string path, bool includeChildren);
        public abstract Task<int> GetNodeCountAsync(string path);
        public abstract Task<int> GetVersionCountAsync(string path);

        /* =============================================================================================== Infrastructure */

        public abstract Task InstallInitialDataAsync(InitialData data);

        /* =============================================================================================== Tools */

        public abstract Task<long> GetNodeTimestampAsync(int nodeId);
        public abstract Task<long> GetVersionTimestampAsync(int versionId);
    }
}
