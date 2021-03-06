using System.Threading;
using System.Threading.Tasks;
using GraphQL.Execution;

namespace GraphQL.Conventions.Adapters.Engine.Listeners.DataLoader
{
    public class DataLoaderListener : DocumentExecutionListenerBase<IDataLoaderContextProvider>
    {
        public override async Task BeforeExecutionAwaitedAsync(
            IDataLoaderContextProvider userContext,
            CancellationToken token)
        {
            await userContext.FetchData(token).ConfigureAwait(false);
        }
    }
}