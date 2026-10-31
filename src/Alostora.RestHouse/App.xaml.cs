using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
   var builder=WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://0.0.0.0:8096"); builder.Services.AddSingleton(new LibraryService(library));
   _server=builder.Build(); _server.UseDefaultFiles(); _server.UseStaticFiles();
   _server.MapGet("/api/health",()=>Results.Ok(new{name="ALOSTORA REST HOUSE",status="running",version="0.1.0"}));
   _server.MapGet("/api/library",(LibraryService svc)=>Results.Ok(svc.List()));
   _server.MapGet("/media/{id}",(string id,LibraryService svc)=>{var item=svc.Find(id);return item is null?Results.NotFound():Results.File(item.FullPath,item.ContentType,enableRangeProcessing:true);});
   await _server.StartAsync(); new MainWindow(library).Show();
  } catch(Exception ex){MessageBox.Show("تعذر تشغيل السيرفر:\n"+ex.Message,"استراحة الأسطورة",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
 }
 private async void OnExit(object sender,ExitEventArgs e){if(_server is not null){await _server.StopAsync();await _server.DisposeAsync();}}
}
