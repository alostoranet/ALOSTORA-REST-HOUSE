using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Net;
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
   var posters=Path.Combine(root,"posters"); Directory.CreateDirectory(posters);
   var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
   var builder=WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, WebRootPath = Directory.Exists(webRoot) ? webRoot : null });
   builder.WebHost.UseUrls("http://0.0.0.0:8096");
   builder.Services.AddSingleton(new LibraryService(library));
   builder.Services.AddSingleton(new WatchProgressService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton(new VideoMetadataService(Path.Combine(root,"resthouse.db")));
   _server=builder.Build(); _server.UseDefaultFiles(); _server.UseStaticFiles();
   static bool IsLocal(HttpContext c) => c.Connection.RemoteIpAddress is not null && IPAddress.IsLoopback(c.Connection.RemoteIpAddress);
   _server.MapGet("/api/health",()=>Results.Ok(new{name="ALOSTORA REST HOUSE",status="running",version="0.3.0"}));
   _server.MapGet("/api/library",(LibraryService svc,VideoMetadataService meta)=>Results.Ok(svc.List().Select(v=>{var m=meta.Get(v.Id);return new{id=v.Id,title=m?.Title??v.Title,fileName=v.FileName,url=v.Url,sizeBytes=v.SizeBytes,description=m?.Description??"",year=m?.Year,genre=m?.Genre??"",kind=m?.Kind??"Movie",posterPath=m?.PosterPath};})));
   _server.MapGet("/api/admin/metadata",(HttpContext c,VideoMetadataService meta)=>IsLocal(c)?Results.Ok(meta.List()):Results.NotFound());
   _server.MapPost("/api/admin/metadata",(HttpContext c,SaveVideoMetadataRequest request,LibraryService librarySvc,VideoMetadataService meta)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.VideoId)||request.VideoId.Length!=24||!request.VideoId.All(Uri.IsHexDigit)||librarySvc.Resolve(request.VideoId) is null) return Results.BadRequest(new{error="الفيديو غير موجود"});
    if(string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>160||(request.Description?.Length??0)>2000||(request.Genre?.Length??0)>80||(request.Year is not null&&(request.Year<1888||request.Year>DateTime.UtcNow.Year+5))||(request.PosterPath?.Length??0)>200) return Results.BadRequest(new{error="تحقق من البيانات المدخلة"});
    meta.Save(request); return Results.Ok(new{saved=true});
   });
   _server.MapPost("/api/admin/poster/{videoId}",async (HttpContext c,string videoId,LibraryService librarySvc,VideoMetadataService meta)=>{
    if(!IsLocal(c)) return Results.NotFound();
    if(videoId.Length!=24||!videoId.All(Uri.IsHexDigit)) return Results.BadRequest(new{error="معرّف الفيديو غير صالح"});
    var video=librarySvc.Resolve(videoId); if(video is null) return Results.NotFound(new{error="الفيديو غير موجود"});
    if(c.Request.ContentLength is > 5_242_880) return Results.StatusCode(413);
    var contentType=c.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
    var ext=contentType switch {"image/jpeg"=>".jpg","image/png"=>".png","image/webp"=>".webp",_=>null};
    if(ext is null) return Results.BadRequest(new{error="الصيغ المسموحة JPG وPNG وWEBP فقط"});
    await using var ms=new MemoryStream(); await c.Request.Body.CopyToAsync(ms);
    if(ms.Length==0||ms.Length>5_242_880) return Results.BadRequest(new{error="حجم الصورة يجب ألا يتجاوز 5 ميجابايت"});
    var bytes=ms.ToArray();
    var valid=contentType switch {
     "image/jpeg"=>bytes.Length>=3&&bytes[0]==0xFF&&bytes[1]==0xD8&&bytes[2]==0xFF,
     "image/png"=>bytes.Length>=8&&bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
     "image/webp"=>bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP",
     _=>false
    };
    if(!valid) return Results.BadRequest(new{error="محتوى الصورة لا يطابق صيغتها"});
    var fileName=videoId+"-"+Guid.NewGuid().ToString("N")+ext;
    var fullPath=Path.Combine(posters,fileName);
    await File.WriteAllBytesAsync(fullPath,bytes);
    var old=meta.Get(videoId);
    meta.Save(new SaveVideoMetadataRequest(videoId,old?.Title??video.Title,old?.Description??"",old?.Year,old?.Genre??"",old?.Kind??"Movie","/posters/"+fileName));
    return Results.Ok(new{posterPath="/posters/"+fileName});
   });
   _server.MapGet("/posters/{fileName}",(string fileName)=>{
    if(fileName!=Path.GetFileName(fileName)||fileName.Length>120) return Results.NotFound();
    var full=Path.Combine(posters,fileName);
    if(!File.Exists(full)) return Results.NotFound();
    var type=Path.GetExtension(fileName).ToLowerInvariant() switch {".jpg"=>"image/jpeg",".png"=>"image/png",".webp"=>"image/webp",_=>null};
    return type is null?Results.NotFound():Results.File(full,type);
   });
   _server.MapGet("/api/progress/{viewerId}",(string viewerId,WatchProgressService db)=>viewerId.Length<=80?Results.Ok(db.Get(viewerId)):Results.BadRequest());
   _server.MapPost("/api/progress",(WatchProgressRequest request,WatchProgressService db,LibraryService svc)=>{if(string.IsNullOrWhiteSpace(request.ViewerId)||request.ViewerId.Length>80||string.IsNullOrWhiteSpace(request.VideoId)||svc.Resolve(request.VideoId) is null||request.PositionSeconds<0||request.PositionSeconds>864000||request.DurationSeconds<0||request.DurationSeconds>864000)return Results.BadRequest();db.Save(request);return Results.Ok(new{saved=true});});
   _server.MapGet("/media/{id}",(string id,LibraryService svc)=>{var item=svc.Resolve(id);return item is null?Results.NotFound():Results.File(item.FullPath,item.ContentType,enableRangeProcessing:true);});
   await _server.StartAsync(); new MainWindow(library).Show();
  } catch(Exception ex){MessageBox.Show("تعذر تشغيل السيرفر:\n"+ex.Message,"استراحة الأسطورة",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
 }
 private async void OnExit(object sender,ExitEventArgs e){if(_server is not null){await _server.StopAsync();await _server.DisposeAsync();}}
}