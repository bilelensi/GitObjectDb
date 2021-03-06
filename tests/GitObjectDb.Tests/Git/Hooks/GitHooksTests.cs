using GitObjectDb.Git;
using GitObjectDb.Git.Hooks;
using GitObjectDb.Models;
using GitObjectDb.Reflection;
using GitObjectDb.Tests.Assets.Customizations;
using GitObjectDb.Tests.Assets.Models;
using GitObjectDb.Tests.Assets.Utils;
using GitObjectDb.Tests.Git.Backends;
using LibGit2Sharp;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitObjectDb.Tests.Git.Hooks
{
    public class GitHooksTests
    {
        [Test]
        [AutoDataCustomizations(typeof(DefaultContainerCustomization), typeof(ModelCustomization))]
        public void PreCommitWhenPropertyChangeGetsFired(GitHooks sut, ObjectRepository repository, IObjectRepositoryContainer<ObjectRepository> container, Page page, Page newLinkedPage, Signature signature, string message)
        {
            // Arrange
            CommitStartedEventArgs lastEvent = null;
            sut.CommitStarted += (_, args) => lastEvent = args;
            var field = repository.Flatten().OfType<Field>().First(
                f => f.Content.MatchOrDefault(matchLink: l => true));

            // Act
            container.AddRepository(repository, signature, message);
            var composer = new PredicateComposer()
                .And(field, f => f.Name == "modified field name" &&
                                 f.Content == FieldContent.NewLink(new FieldLinkContent(new LazyLink<Page>(container, newLinkedPage))))
                .And(page, p => p.Name == "modified page name");
            var modified = repository.With(composer);
            container.Commit(modified.Repository, signature, message);

            // Assert
            Assert.That(lastEvent, Is.Not.Null);
            Assert.That(lastEvent.Changes, Has.Count.EqualTo(2));
        }

        [Test]
        [AutoDataCustomizations(typeof(DefaultContainerCustomization), typeof(ModelCustomization))]
        public void PreCommitCancelsCommitIfRequested(GitHooks sut, ObjectRepository instance, IObjectRepositoryContainer<ObjectRepository> container, Signature signature, string message)
        {
            // Arrange
            sut.CommitStarted += (_, args) => args.Cancel = true;

            // Act
            var update = container.AddRepository(instance, signature, message);

            // Assert
            Assert.That(update, Is.Null);
        }

        [Test]
        [AutoDataCustomizations(typeof(DefaultContainerCustomization), typeof(ModelCustomization))]
        public void PostCommitWhenPropertyChangeGetsFired(GitHooks sut, ObjectRepository instance, IObjectRepositoryContainer<ObjectRepository> container, Page page, Signature signature, string message)
        {
            // Arrange
            CommitCompletedEventArgs lastEvent = null;
            sut.CommitCompleted += (_, args) => lastEvent = args;

            // Act
            container.AddRepository(instance, signature, message);
            var modifiedPage = page.With(p => p.Name == "modified");
            var commit = container.Commit(modifiedPage.Repository, signature, message);

            // Assert
            Assert.That(lastEvent, Is.Not.Null);
            Assert.That(lastEvent.CommitId, Is.EqualTo(commit.CommitId));
        }

        [Test]
        [AutoDataCustomizations(typeof(DefaultContainerCustomization), typeof(ModelCustomization))]
        public void PreMergeGetsFiredWhenPulling(GitHooks sut, ObjectRepository instance, IObjectRepositoryContainer<ObjectRepository> origin, IObjectRepositoryContainerFactory containerFactory, Signature signature, string message)
        {
            // Arrange - Create origin and local repositories
            instance = origin.AddRepository(instance, signature, message);
            var tempPath = RepositoryFixture.GetAvailableFolderPath();
            var clientContainer = containerFactory.Create<ObjectRepository>(tempPath);
            clientContainer.Clone(origin.Repositories.Single().RepositoryDescription.Path);

            // Arrange - Commit change on origin
            var change = instance.Applications[0].Pages[0].With(a => a.Description == "foo");
            origin.Commit(change.Repository, signature, message);

            // Arrange - suscribe to hook
            MergeStartedEventArgs lastEvent = null;
            sut.MergeStarted += (_, args) => lastEvent = args;

            // Act - Pull commit from origin
            var pullResult = clientContainer.Pull(clientContainer.Repositories.Single());
            var result = pullResult.Apply(signature);

            // Assert
            Assert.That(lastEvent, Is.Not.Null);
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        [AutoDataCustomizations(typeof(DefaultContainerCustomization), typeof(ModelCustomization))]
        public void PreMergeCancelsPullWhenRequested(GitHooks sut, ObjectRepository instance, IObjectRepositoryContainer<ObjectRepository> origin, IObjectRepositoryContainerFactory containerFactory, Signature signature, string message)
        {
            // Arrange - Create origin and local repositories
            instance = origin.AddRepository(instance, signature, message);
            var tempPath = RepositoryFixture.GetAvailableFolderPath();
            var clientContainer = containerFactory.Create<ObjectRepository>(tempPath);
            clientContainer.Clone(origin.Repositories.Single().RepositoryDescription.Path);

            // Arrange - Commit change on origin
            var change = instance.Applications[0].Pages[0].With(a => a.Description == "foo");
            origin.Commit(change.Repository, signature, message);

            // Arrange - suscribe to hook
            sut.MergeStarted += (_, args) => args.Cancel = true;

            // Act - Pull commit from origin
            var pullResult = clientContainer.Pull(clientContainer.Repositories.Single());
            var result = pullResult.Apply(signature);

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        [AutoDataCustomizations(typeof(DefaultContainerCustomization), typeof(ModelCustomization))]
        public void PostMergeGetsFiredWhenPulling(GitHooks sut, ObjectRepository instance, IObjectRepositoryContainer<ObjectRepository> origin, IObjectRepositoryContainerFactory containerFactory, Signature signature, string message)
        {
            // Arrange - Create origin and local repositories
            instance = origin.AddRepository(instance, signature, message);
            var tempPath = RepositoryFixture.GetAvailableFolderPath();
            var clientContainer = containerFactory.Create<ObjectRepository>(tempPath);
            clientContainer.Clone(origin.Repositories.Single().RepositoryDescription.Path);

            // Arrange - Commit change on origin
            var change = instance.Applications[0].Pages[0].With(a => a.Description == "foo");
            origin.Commit(change.Repository, signature, message);

            // Arrange - suscribe to hook
            MergeCompletedEventArgs lastEvent = null;
            sut.MergeCompleted += (_, args) => lastEvent = args;

            // Act - Pull commit from origin
            var pullResult = clientContainer.Pull(clientContainer.Repositories.Single());
            pullResult.Apply(signature);

            // Assert
            Assert.That(lastEvent, Is.Not.Null);
        }
    }
}
