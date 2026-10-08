using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Fetchline.Web;

// An explicit entry point, as in the command line: top-level statements would declare a class
// called Program, and that is what the engine calls an assembled program.
internal static class EntryPoint
{
    private static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");
        await builder.Build().RunAsync();
    }
}
