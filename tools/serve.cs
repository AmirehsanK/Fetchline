#:sdk Microsoft.NET.Sdk.Web
#:property TreatWarningsAsErrors=false

// Serves a folder of static files, so that the published playground can be looked at as a web
// server would serve it: trimmed, fingerprinted, with no development server behind it.
//
//   dotnet publish src/Fetchline.Web -c Release -o artifacts/site
//   dotnet run tools/serve.cs -- ../artifacts/site/wwwroot 5196
//
// The folder is relative to this file, because that is where "dotnet run" starts a file like
// this one. It is a development tool and nothing more: it listens on localhost only.

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var folder = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var port = args.Length > 1 ? args[1] : "5196";
if (!Directory.Exists(folder))
{
    Console.Error.WriteLine($"serve: there is no folder '{folder}'");
    return 2;
}

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var files = new PhysicalFileProvider(folder);
var types = new FileExtensionContentTypeProvider();
types.Mappings[".wasm"] = "application/wasm";
types.Mappings[".dat"] = "application/octet-stream";
types.Mappings[".blat"] = "application/octet-stream";

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types });

Console.WriteLine($"serve: {folder} at http://localhost:{port}");
app.Run();
return 0;
