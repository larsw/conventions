using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GraphQL.Conventions.Adapters.Engine.Listeners.DataLoader;
using GraphQL.Conventions.Execution;
using GraphQL.Conventions.Tests.Server.Data.Repositories;
using GraphQL.Conventions.Tests.Server.Schema.Types;
using GraphQL.Conventions.Types;
using GraphQL.Conventions.Types.Relay;

namespace GraphQL.Conventions.Tests.Server
{
    public class UserContext : IUserContext, IDataLoaderContextProvider
    {
        private readonly IBookRepository _bookRepository = new BookRepository();

        private readonly IAuthorRepository _authorRepository = new AuthorRepository();

        public Task<T> Get<T>(Id id)
            where T : class
        {
            if (id.IsIdentifierForType<Book>())
            {
                var dto = _bookRepository.GetBookById(int.Parse(id.IdentifierForType<Book>()));
                var result = new Book(dto) as T;
                return Task.FromResult(result);
            }
            else if (id.IsIdentifierForType<Author>())
            {
                var dto = _authorRepository.GetAuthorById(int.Parse(id.IdentifierForType<Author>()));
                var result = new Author(dto) as T;
                return Task.FromResult(result);
            }
            return new Task<T>(() => null);
        }

        public IEnumerable<INode> Search(string searchString)
        {
            foreach (var dto in _bookRepository.SearchForBooksByTitle(searchString))
            {
                yield return new Book(dto);
            }
            foreach (var dto in _authorRepository.SearchForAuthorsByLastName(searchString))
            {
                yield return new Author(dto);
            }
        }

        public Task FetchData(CancellationToken token)
        {
            return Task.CompletedTask;
        }
    }
}
