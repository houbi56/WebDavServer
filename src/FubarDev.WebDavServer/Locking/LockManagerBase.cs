// <copyright file="LockManagerBase.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FubarDev.WebDavServer.FileSystem;
using FubarDev.WebDavServer.Model.Headers;
using FubarDev.WebDavServer.Models;

using Microsoft.Extensions.Logging;

using IfHeader = FubarDev.WebDavServer.Model.Headers.IfHeader;

namespace FubarDev.WebDavServer.Locking
{
    /// <summary>
    /// The base implementation for an <see cref="ILockManager"/>
    /// </summary>
    /// <remarks>
    /// The derived class must implement <see cref="BeginTransactionAsync"/> and
    /// return an object that implements <see cref="ILockManagerTransaction"/>.
    /// </remarks>
    public abstract class LockManagerBase : ILockManager
    {
        private static readonly Uri _baseUrl = new Uri("http://localhost/");

        private readonly ILockCleanupTask _cleanupTask;

        private readonly ISystemClock _systemClock;

        private readonly ILogger _logger;

        private readonly ILockTimeRounding _rounding;

        /// <summary>
        /// Initializes a new instance of the <see cref="LockManagerBase"/> class.
        /// </summary>
        /// <param name="cleanupTask">The clean-up task for expired locks.</param>
        /// <param name="systemClock">The system clock interface.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="options">The options of the lock manager.</param>
        protected LockManagerBase(
            ILockCleanupTask cleanupTask,
            ISystemClock systemClock,
            ILogger logger,
            ILockManagerOptions? options = null)
        {
            _rounding = options?.Rounding ?? new DefaultLockTimeRounding(DefaultLockTimeRoundingMode.OneSecond);
            _cleanupTask = cleanupTask;
            _systemClock = systemClock;
            _logger = logger;
        }

        /// <inheritdoc />
        public event EventHandler<LockEventArgs>? LockAdded;

        /// <inheritdoc />
        public event EventHandler<LockEventArgs>? LockReleased;

        private enum LockCompareResult
        {
            RightIsParent,
            LeftIsParent,
            Reference,
            NoMatch,
        }

        /// <summary>
        /// This interface must be implemented by the inheriting class.
        /// </summary>
        protected interface ILockManagerTransaction : IDisposable
        {
            /// <summary>
            /// Gets all active locks.
            /// </summary>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns>The collection of all active locks.</returns>
            Task<IReadOnlyCollection<IActiveLock>> GetActiveLocksAsync(CancellationToken cancellationToken);

            /// <summary>
            /// Adds a new active lock.
            /// </summary>
            /// <param name="activeLock">The active lock to add.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns><see langword="true"/> when adding the lock succeeded.</returns>
            Task<bool> AddAsync(IActiveLock activeLock, CancellationToken cancellationToken);

            /// <summary>
            /// Updates the active lock.
            /// </summary>
            /// <param name="activeLock">The active lock with the updated values.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns><see langword="true"/> when the lock was updated, <see langword="false"/> when the lock was added instead.</returns>
            Task<bool> UpdateAsync(IActiveLock activeLock, CancellationToken cancellationToken);

            /// <summary>
            /// Removes an active lock with the given <paramref name="stateToken"/>.
            /// </summary>
            /// <param name="stateToken">The state token of the active lock to remove.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns><see langword="true"/> when a lock with the given <paramref name="stateToken"/> existed and could be removed.</returns>
            Task<bool> RemoveAsync(string stateToken, CancellationToken cancellationToken);

            /// <summary>
            /// Gets an active lock by its <paramref name="stateToken"/>.
            /// </summary>
            /// <param name="stateToken">The state token to search for.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns>The active lock for the state token or <see langword="null"/> when the lock wasn't found.</returns>
            Task<IActiveLock?> GetAsync(string stateToken, CancellationToken cancellationToken);

            /// <summary>
            /// Commits the changes made during the transaction.
            /// </summary>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns>The async task.</returns>
            Task CommitAsync(CancellationToken cancellationToken);
        }

        /// <inheritdoc />
        public int Cost { get; } = 0;

        /// <summary>
        /// Gets the lock cleanup task.
        /// </summary>
        protected ILockCleanupTask LockCleanupTask => _cleanupTask;

        /// <inheritdoc />
        public async Task<LockResult> LockAsync(ILock requestedLock, CancellationToken cancellationToken)
        {
            ActiveLock newActiveLock;
            var destinationUrl = BuildUrl(requestedLock.Path);
            using (var transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                var locks = await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false);
                var status = Find(locks, destinationUrl, requestedLock.Recursive, true);
                var conflictingLocks = GetConflictingLocks(status, requestedLock);
                if (conflictingLocks.Count != 0)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "Found conflicting locks for {Lock}: {ConflictingLocks}",
                            requestedLock,
                            string.Join(",", conflictingLocks.GetLocks().Select(x => x.ToString())));
                    }

                    return new LockResult(conflictingLocks);
                }

                newActiveLock = new ActiveLock(requestedLock, _rounding.Round(_systemClock.UtcNow), _rounding.Round(requestedLock.Timeout));

                await transaction.AddAsync(newActiveLock, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            OnLockAdded(newActiveLock);

            _cleanupTask.Add(this, newActiveLock);

            return new LockResult(newActiveLock);
        }

        /// <inheritdoc />
        public async Task<IImplicitLock> LockImplicitAsync(
            IFileSystem rootFileSystem,
            IReadOnlyCollection<IfHeader>? ifHeaderLists,
            ILock lockRequirements,
            CancellationToken cancellationToken)
        {
            if (ifHeaderLists == null || ifHeaderLists.Count == 0)
            {
                var newLock = await LockAsync(lockRequirements, cancellationToken).ConfigureAwait(false);
                return new ImplicitLock(this, newLock);
            }

            var conditionResults = await FindMatchingIfConditionListAsync(
                rootFileSystem,
                ifHeaderLists,
                lockRequirements,
                cancellationToken).ConfigureAwait(false);
            if (conditionResults == null || conditionResults.Count == 0)
            {
                // No if conditions found for the requested path
                var newLock = await LockAsync(lockRequirements, cancellationToken).ConfigureAwait(false);
                return new ImplicitLock(this, newLock);
            }

            var successfulConditions = conditionResults.Where(x => x.IsSuccess).ToList();
            var firstConditionWithStateToken = successfulConditions
                .FirstOrDefault(x => x.Conditions.RequiresStateToken);
            if (firstConditionWithStateToken != null)
            {
                // Returns the list of locks matched by the first if list
                var usedLocks = firstConditionWithStateToken
                    .Conditions.Conditions
                    .Where(x => x.StateToken != null && !x.Not)
                    .Select(x => firstConditionWithStateToken.TokenToLock[x.StateToken!]).ToList();
                return new ImplicitLock(usedLocks);
            }

            if (successfulConditions.Count != 0)
            {
                // At least one "If" header condition was successful, but we didn't find any with a state token
                var newLock = await LockAsync(lockRequirements, cancellationToken).ConfigureAwait(false);
                return new ImplicitLock(this, newLock);
            }

            // All "If" header conditions were unsuccessful
            var firstFailedCondition = conditionResults
                .FirstOrDefault(x => !x.IsSuccess && x.ActiveLocks.Count != 0);
            if (firstFailedCondition != null)
            {
                var lockResult = new LockResult(
                    new LockStatus(
                        firstFailedCondition.ActiveLocks,
                        Array.Empty<IActiveLock>(),
                        Array.Empty<IActiveLock>()));
                return new ImplicitLock(this, lockResult);
            }

            return new ImplicitLock();
        }

        /// <inheritdoc />
        public async Task<LockRefreshResult> RefreshLockAsync(IFileSystem rootFileSystem, IfHeader ifHeader, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var failedHrefs = new HashSet<Uri>();
            var refreshedLocks = new List<ActiveLock>();

            var pathToInfo = new Dictionary<Uri, PathInfo>();
            using (var transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var ifHeaderList in ifHeader.Lists.Where(x => x.RequiresStateToken))
                {
                    if (!pathToInfo.TryGetValue(ifHeaderList.Path, out var pathInfo))
                    {
                        var destinationUrl = BuildUrl(ifHeaderList.Path.OriginalString);
                        var entryLocks =
                            (from l in await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false)
                             let lockUrl = BuildUrl(l.Path)
                             let compareResult = Compare(destinationUrl, false, lockUrl, l.Recursive)
                             where compareResult == LockCompareResult.Reference
                                   || compareResult == LockCompareResult.RightIsParent
                             select l).ToList();

                        if (entryLocks.Count == 0)
                        {
                            // No lock found for entry
                            failedHrefs.Add(ifHeaderList.RelativeHref);
                            continue;
                        }

                        pathInfo = new PathInfo(entryLocks);
                        pathToInfo.Add(ifHeaderList.Path, pathInfo);
                    }

                    if (pathInfo.EntityTag == null && ifHeaderList.RequiresEntityTag)
                    {
                        var selectionResult = await rootFileSystem
                            .SelectAsync(ifHeaderList.Path.OriginalString, cancellationToken).ConfigureAwait(false);
                        if (selectionResult.IsMissing)
                        {
                            // Probably locked entry not found
                            continue;
                        }

                        var entityTag = await selectionResult.TargetEntry
                            .GetEntityTagAsync(cancellationToken).ConfigureAwait(false);
                        if (entityTag != null)
                        {
                            pathInfo = pathInfo.WithEntityTag(entityTag.Value);
                        }
                    }

                    var foundLock = pathInfo.TokenToLock
                        .Where(x => ifHeaderList.IsMatch(pathInfo.EntityTag, new[] { x.Key })).Select(x => x.Value)
                        .SingleOrDefault();
                    if (foundLock != null)
                    {
                        var refreshedLock = Refresh(foundLock, _rounding.Round(_systemClock.UtcNow), _rounding.Round(timeout));

                        // Remove old lock from clean-up task
                        _cleanupTask.Remove(foundLock);

                        refreshedLocks.Add(refreshedLock);
                    }
                    else
                    {
                        failedHrefs.Add(ifHeaderList.RelativeHref);
                    }
                }

                if (refreshedLocks.Count == 0)
                {
                    var hrefs = failedHrefs.ToList();
                    var href = hrefs.First().OriginalString;
                    var hrefItems = hrefs
                        .Skip(1)
                        .Select(x => x.OriginalString)
                        .Cast<object>()
                        .ToArray();
                    var hrefItemNames = hrefItems.Select(_ => ItemsChoiceType2.href).ToArray();

                    return new LockRefreshResult(
                        new response()
                        {
                            href = href,
                            Items = hrefItems,
                            ItemsElementName = hrefItemNames,
                            error = new error()
                            {
                                Items = new[] { new object(), },
                                ItemsElementName = new[] { ItemsChoiceType.locktokenmatchesrequesturi, },
                            },
                        });
                }

                foreach (var newLock in refreshedLocks)
                {
                    await transaction.UpdateAsync(newLock, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var newLock in refreshedLocks)
            {
                _cleanupTask.Add(this, newLock);
            }

            return new LockRefreshResult(refreshedLocks);
        }

        /// <inheritdoc />
        public async Task<LockReleaseStatus> ReleaseAsync(string path, Uri stateToken, CancellationToken cancellationToken)
        {
            IActiveLock? activeLock;
            using (var transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                activeLock = await transaction.GetAsync(stateToken.OriginalString, cancellationToken).ConfigureAwait(false);
                if (activeLock == null)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "Tried to remove non-existent lock {StateToken}",
                            stateToken);
                    }

                    return LockReleaseStatus.NoLock;
                }

                var destinationUrl = BuildUrl(path);
                var lockUrl = BuildUrl(activeLock.Path);
                var lockCompareResult = Compare(lockUrl, activeLock.Recursive, destinationUrl, false);
                if (lockCompareResult != LockCompareResult.Reference)
                {
                    return LockReleaseStatus.InvalidLockRange;
                }

                await transaction.RemoveAsync(stateToken.OriginalString, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            _cleanupTask.Remove(activeLock);

            OnLockReleased(activeLock);

            return LockReleaseStatus.Success;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<IActiveLock>> GetLocksAsync(CancellationToken cancellationToken)
        {
            using var transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var locks = await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false);
            return locks;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<IActiveLock>> GetAffectedLocksAsync(string path, bool findChildren, bool findParents, CancellationToken cancellationToken)
        {
            var destinationUrl = BuildUrl(path);
            LockStatus status;
            using (var transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                var locks = await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false);
                status = Find(locks, destinationUrl, findChildren, findParents);
            }

            return status.ParentLocks.Concat(status.ReferenceLocks).Concat(status.ChildLocks);
        }

        /// <summary>
        /// Converts a client path to a system path.
        /// </summary>
        /// <remarks>
        /// <para>The client path has the form <c>http://localhost/root-file-system/relative/path</c> and is
        /// therefore always an absolute path. The returned path must be absolute too and might have
        /// the form <c>http://localhost/c/relative/path</c> or something similar. It is of utmost
        /// importance that the URI is always stable. The default implementation of this function
        /// doesn't make any conversions, because it assumes that the same path path always points
        /// to the same file system entry for all clients.</para>
        /// <para>
        /// A URI to a directory must always end in a slash (<c>/</c>).
        /// </para>
        /// </remarks>
        /// <param name="path">The client path to convert</param>
        /// <returns>The system path to be converted to.</returns>
        protected virtual Uri NormalizePath(Uri path)
        {
            return path;
        }

        /// <summary>
        /// Gets called when a lock was added.
        /// </summary>
        /// <param name="activeLock">The lock that was added.</param>
        protected virtual void OnLockAdded(IActiveLock activeLock)
        {
            LockAdded?.Invoke(this, new LockEventArgs(activeLock));
        }

        /// <summary>
        /// Gets called when a lock was released.
        /// </summary>
        /// <param name="activeLock">The lock that was released.</param>
        protected virtual void OnLockReleased(IActiveLock activeLock)
        {
            LockReleased?.Invoke(this, new LockEventArgs(activeLock));
        }

        /// <summary>
        /// Begins a new transaction.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The transaction to be used to update the active locks.</returns>
        protected abstract Task<ILockManagerTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

        private static LockStatus GetConflictingLocks(LockStatus affectingLocks, ILock l)
        {
            var shareMode = LockShareMode.Parse(l.ShareMode);
            if (shareMode == LockShareMode.Exclusive)
            {
                return affectingLocks;
            }

            return new LockStatus(
                affectingLocks
                    .ReferenceLocks
                    .Where(x => LockShareMode.Parse(x.ShareMode) == LockShareMode.Exclusive)
                    .ToList(),
                affectingLocks
                    .ParentLocks
                    .Where(x => LockShareMode.Parse(x.ShareMode) == LockShareMode.Exclusive)
                    .ToList(),
                affectingLocks
                    .ChildLocks
                    .Where(x => LockShareMode.Parse(x.ShareMode) == LockShareMode.Exclusive)
                    .ToList());
        }

        /// <summary>
        /// Returns a new active lock whose new expiration date/time is recalculated using <paramref name="lastRefresh"/> and <paramref name="timeout"/>.
        /// </summary>
        /// <param name="activeLock">The active lock to refresh.</param>
        /// <param name="lastRefresh">The date/time of the last refresh.</param>
        /// <param name="timeout">The new timeout to apply to the lock.</param>
        /// <returns>The new (refreshed) active lock.</returns>
        [Pure]
        private static ActiveLock Refresh(IActiveLock activeLock, DateTime lastRefresh, TimeSpan timeout)
        {
            return new ActiveLock(
                activeLock.Path,
                activeLock.Href,
                activeLock.Recursive,
                activeLock.Owner,
                activeLock.GetOwnerHref(),
                LockAccessType.Parse(activeLock.AccessType),
                LockShareMode.Parse(activeLock.ShareMode),
                timeout,
                activeLock.Issued,
                lastRefresh,
                activeLock.StateToken);
        }

        private async Task<IReadOnlyCollection<PathConditions>?> FindMatchingIfConditionListAsync(
            IFileSystem rootFileSystem,
            IReadOnlyCollection<IfHeader> ifHeaderLists,
            ILock lockRequirements,
            CancellationToken cancellationToken)
        {
            var lockRequirementUrl = BuildUrl(lockRequirements.Path);

            List<IActiveLock> affectingLocks = new();
            using (var transaction = await BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                var locks = await transaction.GetActiveLocksAsync(cancellationToken).ConfigureAwait(false);
                var lockStatus = Find(locks, lockRequirementUrl, lockRequirements.Recursive, true);
                affectingLocks.AddRange(lockStatus.ParentLocks);
                affectingLocks.AddRange(lockStatus.ReferenceLocks);
                if (lockRequirements.Recursive)
                {
                    affectingLocks.AddRange(lockStatus.ChildLocks);
                }
            }

            // Get all If header lists together with all relevant active locks
            Dictionary<IfHeaderList, List<IActiveLock>> ifListLocks = new();
            foreach (var ifHeader in ifHeaderLists)
            {
                foreach (var list in ifHeader.Lists)
                {
                    var listUrl = BuildUrl(list.Path.OriginalString);
                    var compareResult = Compare(listUrl, true, lockRequirementUrl, lockRequirements.Recursive);
                    if (compareResult == LockCompareResult.NoMatch)
                    {
                        continue;
                    }

                    var findRecursive =
                        compareResult == LockCompareResult.LeftIsParent
                        || (compareResult == LockCompareResult.Reference && lockRequirements.Recursive);
                    var foundLocks = list.RequiresStateToken
                        ? Find(affectingLocks, listUrl, findRecursive, true)
                        : LockStatus.Empty;
                    var locksForIfConditions = foundLocks.GetLocks().ToList();
                    ifListLocks.Add(list, locksForIfConditions);
                }
            }

            // List of matches between path info and if header lists
            var conditionResults = new List<PathConditions>();
            if (ifListLocks.Count == 0)
            {
                return null;
            }

            // Collect all file system specific information
            var pathToInfo = new Dictionary<Uri, PathInfo>();
            foreach (var matchingIfListItem in ifListLocks)
            {
                var ifHeaderList = matchingIfListItem.Key;
                if (!pathToInfo.TryGetValue(ifHeaderList.Path, out var pathInfo))
                {
                    pathInfo = new PathInfo(matchingIfListItem.Value);
                    pathToInfo.Add(ifHeaderList.Path, pathInfo);
                }

                if (pathInfo.EntityTag == null)
                {
                    if (ifHeaderList.RequiresEntityTag)
                    {
                        var selectionResult = await rootFileSystem
                            .SelectAsync(ifHeaderList.Path.OriginalString, cancellationToken).ConfigureAwait(false);
                        if (!selectionResult.IsMissing)
                        {
                            var entityTag = await selectionResult
                                .TargetEntry.GetEntityTagAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (entityTag != null)
                            {
                                pathInfo = pathInfo.WithEntityTag(entityTag.Value);
                            }
                        }
                    }
                }

                if (pathInfo.LockTokens.Count != 0
                    && ifHeaderList.IsMatch(pathInfo.EntityTag, pathInfo.LockTokens))
                {
                    conditionResults.Add(
                        new PathConditions(
                            pathInfo.ActiveLocks.Any(al => al.IsSameOwner(lockRequirements)),
                            pathInfo,
                            ifHeaderList));
                }
            }

            return conditionResults;
        }

        private LockStatus Find(IEnumerable<IActiveLock> locks, Uri parentUrl, bool withChildren, bool findParents)
        {
            var normalizedParentUrl = NormalizePath(parentUrl);
            var refLocks = new List<IActiveLock>();
            var childLocks = new List<IActiveLock>();
            var parentLocks = new List<IActiveLock>();

            foreach (var activeLock in locks)
            {
                var lockUrl = BuildUrl(activeLock.Path);
                var normalizedLockUrl = NormalizePath(lockUrl);
                var result = Compare(normalizedParentUrl, withChildren, normalizedLockUrl, activeLock.Recursive);
                switch (result)
                {
                    case LockCompareResult.Reference:
                        refLocks.Add(activeLock);
                        break;
                    case LockCompareResult.LeftIsParent:
                        childLocks.Add(activeLock);
                        break;
                    case LockCompareResult.RightIsParent:
                        if (findParents)
                            parentLocks.Add(activeLock);
                        break;
                }
            }

            return new LockStatus(refLocks, parentLocks, childLocks);
        }

        private LockCompareResult Compare(Uri left, bool leftRecursive, Uri right, bool rightRecursive)
        {
            if (left == right)
            {
                return LockCompareResult.Reference;
            }

            if (left.IsBaseOf(right) && leftRecursive)
            {
                return LockCompareResult.LeftIsParent;
            }

            if (right.IsBaseOf(left) && rightRecursive)
            {
                return LockCompareResult.RightIsParent;
            }

            return LockCompareResult.NoMatch;
        }

        private Uri BuildUrl(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return _baseUrl;
            }

            return new Uri(_baseUrl, path + (path.EndsWith("/") ? string.Empty : "/"));
        }

        private class PathInfo
        {
            public PathInfo(IReadOnlyCollection<IActiveLock> activeLocks)
            {
                ActiveLocks = activeLocks;
                TokenToLock = activeLocks
                    .ToDictionary(x => new Uri(x.StateToken, UriKind.RelativeOrAbsolute), x => x);
                LockTokens = TokenToLock.Keys.ToList();
            }

            private PathInfo(
                IReadOnlyCollection<IActiveLock> activeLocks,
                IReadOnlyDictionary<Uri, IActiveLock> tokenToLock,
                IReadOnlyCollection<Uri> lockTokens,
                EntityTag entityTag)
            {
                EntityTag = entityTag;
                ActiveLocks = activeLocks;
                TokenToLock = tokenToLock;
                LockTokens = lockTokens;
            }

            public EntityTag? EntityTag { get; }

            public IReadOnlyCollection<IActiveLock> ActiveLocks { get; }

            public IReadOnlyDictionary<Uri, IActiveLock> TokenToLock { get; }

            public IReadOnlyCollection<Uri> LockTokens { get; }

            public PathInfo WithEntityTag(EntityTag entityTag)
            {
                return new PathInfo(ActiveLocks, TokenToLock, LockTokens, entityTag);
            }
        }

        private class PathConditions
        {
            public PathConditions(bool isSuccess, PathInfo path, IfHeaderList conditions)
            {
                IsSuccess = isSuccess;
                ActiveLocks = path.ActiveLocks;
                TokenToLock = path.TokenToLock;
                Conditions = conditions;
            }

            public bool IsSuccess { get; }

            public IReadOnlyCollection<IActiveLock> ActiveLocks { get; }

            public IReadOnlyDictionary<Uri, IActiveLock> TokenToLock { get; }

            public IfHeaderList Conditions { get; }
        }
    }
}
