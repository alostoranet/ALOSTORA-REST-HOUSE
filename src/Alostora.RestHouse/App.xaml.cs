using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;
namespace Alostora.RestHouse;
public partial class App : Application
{
 private WebApplication? _server;
 private async void OnStartup(object sender, StartupEventArgs e)
 {
  try {
   var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AlostoraRestHouse");
   var library=Path.Combine(root,"Library"); Directory.CreateDirectory(library);
   var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
   var builder=WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, WebRootPath = Directory.Exists(webRoot) ? webRoot : null }); builder.WebHost.UseUrls("http://0.0.0.0:8096"); builder.Services.AddSingleton(new LibraryService(library)); builder.Services.AddSingleton(new WatchProgressService(Path.Combine(root,"resthouse.db")));
   _server=builder.Build(); _server.UseDefaultFiles(); _server.UseStaticFiles();
   _server.MapGet("/api/health",()=>Results.Ok(new{name="ALOSTORA REST HOUSE",status="running",version="0.1.0"}));
   _server.MapGet("/api/library",(LibraryService svc)=>Results.Ok(svc.List()));
   _server.MapGet("/api/progress/{viewerId}",(string viewerId,WatchProgressService db)=>Results.Ok(db.Get(viewerId)));
   _server.MapPost("/api/progress",(WatchProgressRequest request,WatchProgressService db)=>{if(string.IsNullOrWhiteSpace(request.ViewerId)||string.IsNullOrWhiteSpace(request.VideoId)||request.PositionSeconds<0)return Results.BadRequest();db.Save(request);return Results.Ok(new{saved=true});});
   _server.MapGet("/media/{id}",(string id,LibraryService svc)=>{var item=svc.Resolve(id);return item is null?Results.NotFound():Results.File(item.FullPath,item.ContentType,enableRangeProcessing:true);});
   await _server.StartAsync(); new MainWindow(library).Show();
  } catch(Exception ex){MessageBox.Show("تعذر تشغيل السيرفر:\n"+ex.Message,"استراحة الأسطورة",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
 }
 private async void OnExit(object sender,ExitEventArgs e){if(_server is not null){await _server.StopAsync();await _server.DisposeAsync();}}
}
