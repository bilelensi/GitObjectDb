using LibGit2Sharp;
using System;

namespace GitObjectDb.Git
{
    /// <summary>
    /// Provides access to reusable repository instances that get automatically evicted
    /// after a timeout duration.
    /// </summary>
    internal interface IRepositoryProvider
    {
        /// <summary>
        /// Returns the result of the provided function processing.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="description">The description.</param>
        /// <param name="function">The function.</param>
        /// <returns>The result of the function call.</returns>
        TResult Execute<TResult>(RepositoryDescription description, Func<IRepository, TResult> function);
    }
}