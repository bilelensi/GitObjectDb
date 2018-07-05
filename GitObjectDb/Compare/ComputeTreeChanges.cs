using GitObjectDb.Git;
using GitObjectDb.Models;
using GitObjectDb.Reflection;
using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace GitObjectDb.Compare
{
    /// <inheritdoc/>
    internal class ComputeTreeChanges : IComputeTreeChanges
    {
        readonly IModelDataAccessorProvider _modelDataProvider;
        readonly IInstanceLoader _instanceLoader;
        readonly IRepositoryProvider _repositoryProvider;
        readonly RepositoryDescription _repositoryDescription;
        readonly StringBuilder _jsonBuffer = new StringBuilder();

        /// <summary>
        /// Initializes a new instance of the <see cref="ComputeTreeChanges"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="repositoryDescription">The repository description.</param>
        /// <exception cref="ArgumentNullException">serviceProvider</exception>
        public ComputeTreeChanges(IServiceProvider serviceProvider, RepositoryDescription repositoryDescription)
        {
            _modelDataProvider = serviceProvider.GetRequiredService<IModelDataAccessorProvider>();
            _instanceLoader = serviceProvider.GetRequiredService<IInstanceLoader>();
            _repositoryProvider = serviceProvider.GetRequiredService<IRepositoryProvider>();
            _repositoryDescription = repositoryDescription;
        }

        static void RemoveNode(TreeDefinition definition, Stack<string> stack)
        {
            var path = stack.ToDataPath();
            definition.Remove(path);
        }

        /// <inheritdoc/>
        public MetadataTreeChanges Compare<TInstance>(Func<IRepository, Tree> oldTreeGetter, Func<IRepository, Tree> newTreeGetter)
            where TInstance : AbstractInstance
        {
            return _repositoryProvider.Execute(_repositoryDescription, repository =>
            {
                var oldInstance = _instanceLoader.LoadFrom<TInstance>(_repositoryDescription, oldTreeGetter);
                var newInstance = _instanceLoader.LoadFrom<TInstance>(_repositoryDescription, newTreeGetter);

                var oldTree = oldTreeGetter(repository);
                var newTree = newTreeGetter(repository);
                var changes = repository.Diff.Compare<TreeChanges>(oldTree, newTree);

                ThrowIfNonSupportedChangeTypes(changes);

                var modified = CollectModifiedNodes(oldInstance, newInstance, changes, oldTree);
                var added = CollectAddedNodes(newInstance, changes, newTree);
                var deleted = CollectDeletedNodes(oldInstance, changes, oldTree);
                return new MetadataTreeChanges(added, modified, deleted);
            });
        }

        static IImmutableList<MetadataTreeEntryChanges> CollectModifiedNodes(AbstractInstance oldInstance, AbstractInstance newInstance, TreeChanges changes, Tree oldTree) =>
            (from c in changes.Modified
             let oldEntry = oldTree[c.Path]
             where oldEntry.TargetType == TreeEntryTargetType.Blob
             let path = c.Path.ParentPath()
             let oldNode = oldInstance.TryGetFromGitPath(path) ?? throw new NotSupportedException($"Node {path} could not be found in old instance.")
             let newNode = newInstance.TryGetFromGitPath(path) ?? throw new NotSupportedException($"Node {path} could not be found in new instance.")
             select new MetadataTreeEntryChanges(oldNode, newNode))
            .ToImmutableList();

        static IImmutableList<MetadataTreeEntryChanges> CollectAddedNodes(AbstractInstance newInstance, TreeChanges changes, Tree newTree) =>
            (from c in changes.Added
             let newEntry = newTree[c.Path]
             where newEntry.TargetType == TreeEntryTargetType.Blob
             let path = c.Path.ParentPath()
             let newNode = newInstance.TryGetFromGitPath(path) ?? throw new NotSupportedException($"Node {path} could not be found in new instance.")
             select new MetadataTreeEntryChanges(null, newNode))
            .ToImmutableList();

        static IImmutableList<MetadataTreeEntryChanges> CollectDeletedNodes(AbstractInstance oldInstance, TreeChanges changes, Tree oldTree) =>
            (from c in changes.Deleted
             let oldEntry = oldTree[c.Path]
             where oldEntry.TargetType == TreeEntryTargetType.Blob
             let path = c.Path.ParentPath()
             let oldNode = oldInstance.TryGetFromGitPath(path) ?? throw new NotSupportedException($"Node {path} could not be found in old instance.")
             select new MetadataTreeEntryChanges(oldNode, null))
            .ToImmutableList();

        static void ThrowIfNonSupportedChangeTypes(TreeChanges changes)
        {
            if (changes.Conflicted.Any())
            {
                throw new NotSupportedException("Conflicting changes is not yet supported.");
            }
            if (changes.Copied.Any())
            {
                throw new NotSupportedException("Copied changes is not yet supported.");
            }
            if (changes.Renamed.Any())
            {
                throw new NotSupportedException("Renamed changes is not yet supported.");
            }
            if (changes.TypeChanged.Any())
            {
                throw new NotSupportedException("TypeChanged changes is not yet supported.");
            }
        }

        /// <inheritdoc/>
        public (TreeDefinition NewTree, bool AnyChange) Compare(AbstractInstance original, AbstractInstance newInstance, IRepository repository)
        {
            if (original == null)
            {
                throw new ArgumentNullException(nameof(original));
            }
            if (newInstance == null)
            {
                throw new ArgumentNullException(nameof(newInstance));
            }

            var tree = original._getTree(repository);
            var definition = TreeDefinition.From(tree);
            var stack = new Stack<string>();
            var anyChange = CompareNode(original, newInstance, repository, tree, definition, stack);
            return (definition, anyChange);
        }

        bool CompareNode(IMetadataObject original, IMetadataObject @new, IRepository repository, Tree tree, TreeDefinition definition, Stack<string> stack)
        {
            var anyChange = false;
            var accessor = _modelDataProvider.Get(original.GetType());
            UpdateNodeIfNeeded(original, @new, repository, definition, stack, accessor, ref anyChange);
            foreach (var childProperty in accessor.ChildProperties)
            {
                if (!childProperty.ShouldVisitChildren(original) && !childProperty.ShouldVisitChildren(@new))
                {
                    // Do not visit children if they were not generated.
                    // This means that they were not modified!
                    continue;
                }

                stack.Push(childProperty.Property.Name);
                anyChange |= CompareNodeChildren(original, @new, repository, tree, definition, stack, childProperty);
                stack.Pop();
            }
            return anyChange;
        }

        bool CompareNodeChildren(IMetadataObject original, IMetadataObject @new, IRepository repository, Tree tree, TreeDefinition definition, Stack<string> stack, ChildPropertyInfo childProperty)
        {
            var anyChange = false;
            using (var enumerator = new TwoSequenceEnumerator<IMetadataObject>(
                childProperty.Accessor(original),
                childProperty.Accessor(@new)))
            {
                while (!enumerator.BothCompleted)
                {
                    anyChange |= CompareNodeChildren(repository, tree, definition, stack, enumerator);
                }
            }
            return anyChange;
        }

        bool CompareNodeChildren(IRepository repository, Tree tree, TreeDefinition definition, Stack<string> stack, TwoSequenceEnumerator<IMetadataObject> enumerator)
        {
            if (enumerator.NodeIsStillThere)
            {
                stack.Push(enumerator.Left.Id.ToString());
                var anyChange = CompareNode(enumerator.Left, enumerator.Right, repository, tree, definition, stack);
                stack.Pop();

                enumerator.MoveNextLeft();
                enumerator.MoveNextRight();
                return anyChange;
            }
            else if (enumerator.NodeHasBeenAdded)
            {
                stack.Push(enumerator.Right.Id.ToString());
                AddOrUpdateNode(enumerator.Right, repository, definition, stack);
                stack.Pop();
                enumerator.MoveNextRight();
                return true;
            }
            else if (enumerator.NodeHasBeenRemoved)
            {
                stack.Push(enumerator.Left.Id.ToString());
                RemoveNode(definition, stack);
                stack.Pop();
                enumerator.MoveNextLeft();
                return true;
            }
            throw new NotSupportedException("Unexpected child changes.");
        }

        void UpdateNodeIfNeeded(IMetadataObject original, IMetadataObject @new, IRepository repository, TreeDefinition definition, Stack<string> stack, IModelDataAccessor accessor, ref bool anyChange)
        {
            if (accessor.ModifiableProperties.Any(p => !p.AreSame(original, @new)))
            {
                AddOrUpdateNode(@new, repository, definition, stack);
                anyChange = true;
            }
        }

        void AddOrUpdateNode(IMetadataObject node, IRepository repository, TreeDefinition definition, Stack<string> stack)
        {
            var path = stack.ToDataPath();
            node.ToJson(_jsonBuffer);
            definition.Add(path, repository.CreateBlob(_jsonBuffer), Mode.NonExecutableFile);
        }
    }
}