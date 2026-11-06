using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Alostora.RestHouse;

public sealed record BulkLibraryRequest(List<string> Ids);
public sealed record CustomerMessageStatusRequest(string Status);
public sealed record UpdateSeasonFolderRequest(string? FolderPath);
public sealed record ScanFolderRequest(string FolderPath);
public sealed record AddMediaLibraryRootRequest(string Kind, string FolderPath);
public sealed record RegisterSelectedFilesRequest(string FolderPath, List<string> FilePaths);
public sealed record RegisterFilesRequest(List<string> FilePaths);
public sealed record CreateLibraryFolderRequest(string? ParentPath, string Name);
public sealed record UnregisterLibraryFolderRequest(string FolderPath);
public sealed record DetectedEpisodeLink(string VideoId, int EpisodeNumber, string? Title, bool Selected, string? FilePath = null);
public sealed record LinkDetectedEpisodesRequest(List<DetectedEpisodeLink> Episodes);
public sealed record RelinkEpisodeRequest(string VideoId);
public sealed record UpdateEpisodeRequest(int EpisodeNumber, string? Title);
public sealed record PreparePlaybackRequest(int? AudioStreamIndex);

public partial class App : Application
{
 private WebApplication? _server;
 private static readonly HttpClient SupabaseAuthHttpClient = new();
 private static readonly object DiagnosticLogLock = new();

 internal static void LogDiagnostic(string area, Exception exception)
 {
  try
  {
   var logPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "AlostoraRestHouse","logs","application-errors.log");
   lock(DiagnosticLogLock)
   {
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    File.AppendAllText(logPath,$"[{DateTimeOffset.Now:O}] [{area}] {exception}{Environment.NewLine}{exception.StackTrace}{Environment.NewLine}{Environment.NewLine}");
   }
  }
  catch { }
 }

 private static void InstallGlobalExceptionLogging()
 {
  Current.DispatcherUnhandledException += (_, e) =>
  {
   LogDiagnostic("WPF Dispatcher",e.Exception);
   e.Handled=true;
   try
   {
    MessageBox.Show("حدث خطأ غير متوقع، وتم حفظ تفاصيله في سجل الأخطاء. سيُغلق التطبيق لحماية البيانات.",
     "استراحة الأسطورة",MessageBoxButton.OK,MessageBoxImage.Error);
   }
   catch { }
   Current.Shutdown(1);
  };
  AppDomain.CurrentDomain.UnhandledException += (_, e) =>
   LogDiagnostic("AppDomain"+(e.IsTerminating?" (terminating)":""),e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown exception"));
  TaskScheduler.UnobservedTaskException += (_, e) =>
  {
   LogDiagnostic("Unobserved Task",e.Exception);
   e.SetObserved();
  };
 }

 private async void OnStartup(object sender, StartupEventArgs e)
 {
  InstallGlobalExceptionLogging();
  var configuredPort = 8097;
  try {
   var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AlostoraRestHouse");
   Directory.CreateDirectory(root);
   var settings=AppSettings.Load(root);
   configuredPort = settings.Port;
   var library=settings.LibraryPath;
   var posters=settings.PostersPath;
   Directory.CreateDirectory(library);
   Directory.CreateDirectory(posters);
   // Do not open a second management window against a server owned by another
   // process: closing the server-owning process would otherwise leave this window orphaned.
   if(await IsExistingRestHouseServerAsync(settings.Port))
   {
    MessageBox.Show("استراحة الأسطورة تعمل بالفعل. استخدم نافذة التطبيق المفتوحة بدلاً من تشغيل نسخة ثانية.",
     "استراحة الأسطورة",MessageBoxButton.OK,MessageBoxImage.Information);
    Shutdown();
    return;
   }
   var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
   var builder=WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, WebRootPath = Directory.Exists(webRoot) ? webRoot : null });
   var adminCertificatePath=Environment.GetEnvironmentVariable("ALOSTORA_HTTPS_CERT_PATH");
   var adminCertificatePassword=Environment.GetEnvironmentVariable("ALOSTORA_HTTPS_CERT_PASSWORD");
   var adminHttpsEnabled=!string.IsNullOrWhiteSpace(adminCertificatePath)&&File.Exists(adminCertificatePath);
   builder.WebHost.ConfigureKestrel(options =>
   {
    options.ListenAnyIP(settings.Port);
    if(adminHttpsEnabled) options.ListenAnyIP(8443,listen=>listen.UseHttps(adminCertificatePath!,adminCertificatePassword));
   });
   var supabaseUrl=(Environment.GetEnvironmentVariable("ALOSTORA_SUPABASE_URL")??"https://erxbnboohjiylzbafqho.supabase.co").TrimEnd('/');
   var supabasePublishableKey=Environment.GetEnvironmentVariable("ALOSTORA_SUPABASE_PUBLISHABLE_KEY")??"sb_publishable_IVA_HybnOmyw0bp-tkJfpw_5VyYr1aV";
   var libraryService = new LibraryService(library,Path.Combine(root,"resthouse.db"));
   builder.Services.AddSingleton(libraryService);
   builder.Services.AddSingleton(new MediaConversionService(libraryService,settings.FfmpegPath,Path.Combine(root,"TranscodeCache")));
   builder.Services.AddSingleton(new WatchProgressService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton(new VideoMetadataService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton(new MultiMediaLibraryService(library,Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton<DownloadProgressService>();
   builder.Services.AddSingleton(new DownloadSpeedLimitService(root));
   builder.Services.AddSingleton(new SeriesCatalogService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton(new CatalogCategoryService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton(new CustomerMessageService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddSingleton(new RestHouseProfileService(root));
   builder.Services.AddSingleton(new AnnouncementService(root));
   builder.Services.AddSingleton(new CustomerAdService(root,posters));
   builder.Services.AddSingleton(new CustomerPageSettingsService(root));
   builder.Services.AddSingleton<MetadataLookupService>();
   builder.Services.AddSingleton(new PublicCatalogAccessService(Path.Combine(root,"resthouse.db")));
   builder.Services.AddHostedService(provider => new ScheduledBackupService(root,posters,provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledBackupService>>()));
   _server=builder.Build();
   static bool IsLanAddress(IPAddress? address)
   {
    if(address is null) return false;
    if(address.IsIPv4MappedToIPv6) address=address.MapToIPv4();
    if(IPAddress.IsLoopback(address)) return true;
    if(address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)
    {
     var b=address.GetAddressBytes();
     return b[0]==10 || (b[0]==172&&b[1]>=16&&b[1]<=31) || (b[0]==192&&b[1]==168) || (b[0]==169&&b[1]==254);
    }
    return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
   }
   _server.Use(async (context,next) =>
   {
    if(!IsLanAddress(context.Connection.RemoteIpAddress))
    {
     context.Response.StatusCode=StatusCodes.Status403Forbidden;
     await context.Response.WriteAsync("ALOSTORA REST HOUSE is available on the local network only.");
     return;
    }
    await next();
   });
   _server.UseDefaultFiles(); _server.UseStaticFiles();
   static bool IsLocal(HttpContext c)
   {
    var address = c.Connection.RemoteIpAddress;
    if(address is null) return false;
    if(address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
    return IPAddress.IsLoopback(address);
   }
   static bool IsAdmin(HttpContext c) => IsLocal(c) || (c.Items.TryGetValue("alostora.admin",out var allowed) && allowed is true);
   _server.Use(async (context,next) =>
   {
    if(!context.Request.Path.StartsWithSegments("/api/admin"))
    {
     await next();
     return;
    }
    if(IsLocal(context) && !context.Request.Headers.ContainsKey("Authorization"))
    {
     context.Items["alostora.admin"]=true;
     await next();
     return;
    }
    if(!context.Request.IsHttps && !IsLocal(context))
    {
     context.Response.StatusCode=StatusCodes.Status426UpgradeRequired;
     await context.Response.WriteAsJsonAsync(new{error="تسجيل دخول الإدارة يتطلب HTTPS. فعّل شهادة TLS على المنفذ 8443."});
     return;
    }
    if(!context.Request.Headers.TryGetValue("Authorization",out var authorization) ||
       !AuthenticationHeaderValue.TryParse(authorization.ToString(),out var bearer) ||
       !string.Equals(bearer.Scheme,"Bearer",StringComparison.OrdinalIgnoreCase) ||
       string.IsNullOrWhiteSpace(bearer.Parameter))
    {
     context.Response.StatusCode=StatusCodes.Status401Unauthorized;
     await context.Response.WriteAsJsonAsync(new{error="يلزم تسجيل الدخول للوصول إلى الإدارة."});
     return;
    }
    if(string.IsNullOrWhiteSpace(supabasePublishableKey))
    {
     context.Response.StatusCode=StatusCodes.Status503ServiceUnavailable;
     await context.Response.WriteAsJsonAsync(new{error="إعدادات Supabase Auth غير مكتملة على الخادم."});
     return;
    }
    try
    {
     using var request=new HttpRequestMessage(HttpMethod.Get,supabaseUrl+"/auth/v1/user");
     request.Headers.TryAddWithoutValidation("apikey",supabasePublishableKey);
     request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer.Parameter);
     using var response=await SupabaseAuthHttpClient.SendAsync(request,context.RequestAborted);
     if(!response.IsSuccessStatusCode)
     {
      context.Response.StatusCode=StatusCodes.Status401Unauthorized;
      await context.Response.WriteAsJsonAsync(new{error="جلسة الدخول غير صالحة أو انتهت. سجّل الدخول مجدداً."});
      return;
     }
     await using var stream=await response.Content.ReadAsStreamAsync(context.RequestAborted);
     using var user=await JsonDocument.ParseAsync(stream,cancellationToken:context.RequestAborted);
     if(!user.RootElement.TryGetProperty("app_metadata",out var metadata) ||
        metadata.ValueKind!=JsonValueKind.Object ||
        !metadata.TryGetProperty("role",out var role) ||
        !string.Equals(role.GetString(),"admin",StringComparison.OrdinalIgnoreCase))
     {
      context.Response.StatusCode=StatusCodes.Status403Forbidden;
      await context.Response.WriteAsJsonAsync(new{error="تم تسجيل الدخول، لكن هذا الحساب لا يملك صلاحية إدارة الاستراحة."});
      return;
     }
     context.Items["alostora.admin"]=true;
    }
    catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested)
    {
     return;
    }
    catch(HttpRequestException)
    {
     context.Response.StatusCode=StatusCodes.Status503ServiceUnavailable;
     await context.Response.WriteAsJsonAsync(new{error="تعذر الاتصال بخدمة التحقق من Supabase. تم رفض طلب الإدارة لحماية النظام."});
     return;
    }
    catch(JsonException)
    {
     context.Response.StatusCode=StatusCodes.Status401Unauthorized;
     await context.Response.WriteAsJsonAsync(new{error="تعذر التحقق من جلسة الدخول."});
     return;
    }
    await next();
   });
   static bool PosterExists(string? posterPath,string postersRoot)
   {
    if(string.IsNullOrWhiteSpace(posterPath)||!posterPath.StartsWith("/posters/",StringComparison.OrdinalIgnoreCase))return false;
    try
    {
     var relative=Uri.UnescapeDataString(posterPath["/posters/".Length..]).Replace('/',Path.DirectorySeparatorChar);
     var rootPath=Path.GetFullPath(postersRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
     var fullPath=Path.GetFullPath(Path.Combine(rootPath,relative));
     return fullPath.StartsWith(rootPath,StringComparison.OrdinalIgnoreCase)&&File.Exists(fullPath);
    }
    catch{return false;}
   }
   _server.MapGet("/api/auth-config",()=>Results.Ok(new{supabaseUrl,publishableKey=supabasePublishableKey}));
   _server.MapGet("/admin",(HttpContext c) =>
   {
    if(IsLocal(c)) return Results.Redirect("/admin.html");
    if(adminHttpsEnabled) return Results.Redirect($"https://{c.Request.Host.Host}:8443/admin");
    return Results.Problem("إدارة الاستراحة عن بُعد تحتاج شهادة HTTPS. اضبط ALOSTORA_HTTPS_CERT_PATH وALOSTORA_HTTPS_CERT_PASSWORD أولاً.",statusCode:503);
   });
   _server.MapGet("/api/health",()=>Results.Ok(new{name="ALOSTORA REST HOUSE",status="running",version="0.3.0"}));
   _server.MapGet("/api/profile",(RestHouseProfileService profile)=>Results.Ok(profile.Load()));
   _server.MapGet("/api/customer-settings",(CustomerPageSettingsService settings)=>Results.Ok(settings.Load()));
   _server.MapGet("/api/admin/customer-settings",(HttpContext c,CustomerPageSettingsService settings)=>
    IsAdmin(c)?Results.Ok(settings.Load()):Results.NotFound());
   _server.MapPost("/api/admin/customer-settings",(HttpContext c,CustomerPageSettings request,CustomerPageSettingsService settings)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(request is null)return Results.BadRequest(new{error="إعدادات واجهة العميل غير صالحة."});
    if(!string.IsNullOrWhiteSpace(request.LiveBroadcastUrl) &&
       (!Uri.TryCreate(request.LiveBroadcastUrl.Trim(),UriKind.Absolute,out var liveUri) ||
        (liveUri.Scheme!=Uri.UriSchemeHttp && liveUri.Scheme!=Uri.UriSchemeHttps) ||
        string.IsNullOrWhiteSpace(liveUri.Host)))
     return Results.BadRequest(new{error="رابط البث يجب أن يبدأ بـ http:// أو https://"});
    var normalized=request with{LiveBroadcastUrl=string.IsNullOrWhiteSpace(request.LiveBroadcastUrl)?null:request.LiveBroadcastUrl.Trim()};
    return Results.Ok(new{saved=true,settings=settings.Save(normalized)});
   });
   _server.MapGet("/api/admin/download-speed-limit",(HttpContext c,DownloadSpeedLimitService speedLimit)=>
    IsAdmin(c)?Results.Ok(speedLimit.GetSettings()):Results.NotFound());
   _server.MapPost("/api/admin/download-speed-limit",(HttpContext c,DownloadSpeedLimitSettings request,DownloadSpeedLimitService speedLimit)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(request is null||request.LimitMbps is < 0 or > 1000)
     return Results.BadRequest(new{error="أدخل حداً بين 0 و1000 ميجابت/ثانية. الصفر يعني دون حد."});
    try{return Results.Ok(new{saved=true,settings=speedLimit.Save(request)});}
    catch(IOException){return Results.Problem("تعذر حفظ إعداد سرعة التنزيل.",statusCode:500);}
   });
   _server.MapGet("/api/customer-ads",(CustomerAdService ads)=>Results.Ok(ads.List(activeOnly:true)));
   _server.MapGet("/api/customer-ads/{id}/image",(string id,CustomerAdService ads)=>
   {
    if(!Guid.TryParseExact(id,"N",out _ )||!ads.TryGetImage(id,out var path,out var contentType))return Results.NotFound();
    return Results.File(path,contentType,enableRangeProcessing:true);
   });
   _server.MapGet("/api/admin/customer-ads",(HttpContext c,CustomerAdService ads)=>
    IsAdmin(c)?Results.Ok(ads.List()):Results.NotFound());
   _server.MapPost("/api/admin/customer-ads",async (HttpContext c,CustomerAdService ads)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(c.Request.ContentLength is > 6*1024*1024)return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    if(!c.Request.HasFormContentType)return Results.BadRequest(new{error="أرسل صورة الإعلان بصيغة نموذج ملفات."});
    try
    {
     var form=await c.Request.ReadFormAsync(c.RequestAborted);
     var file=form.Files.GetFile("image");
     if(file is null||file.Length<=0||file.Length>CustomerAdService.MaximumImageBytes)
      return Results.BadRequest(new{error="اختر صورة لا يتجاوز حجمها 5 ميجابايت."});
     await using var stream=file.OpenReadStream();
     var duration=int.TryParse(form["durationSeconds"].ToString(),out var parsed)?parsed:10;
     var item=ads.Add(stream,file.FileName,form["title"].ToString(),form["linkUrl"].ToString(),duration);
     return Results.Ok(new{saved=true,ad=item});
    }
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(IOException){return Results.Problem("تعذر حفظ صورة الإعلان محلياً.",statusCode:500);}
   });
   _server.MapPost("/api/admin/customer-ads/{id}",(HttpContext c,string id,CustomerAdUpdateRequest request,CustomerAdService ads)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(!Guid.TryParseExact(id,"N",out _ )||request is null)return Results.BadRequest(new{error="بيانات الإعلان غير صالحة."});
    try{return Results.Ok(new{saved=true,ad=ads.Update(id,request)});}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(KeyNotFoundException){return Results.NotFound();}
    catch(IOException){return Results.Problem("تعذر حفظ إعدادات الإعلان.",statusCode:500);}
   });
   _server.MapPost("/api/admin/customer-ads/{id}/move",(HttpContext c,string id,CustomerAdMoveRequest request,CustomerAdService ads)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(!Guid.TryParseExact(id,"N",out _ )||request is null)return Results.BadRequest();
    try{return ads.Move(id,request.Direction)?Results.Ok(new{moved=true}):Results.NotFound();}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
   });
   _server.MapDelete("/api/admin/customer-ads/{id}",(HttpContext c,string id,CustomerAdService ads)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(!Guid.TryParseExact(id,"N",out _))return Results.BadRequest();
    try{return ads.Remove(id)?Results.Ok(new{removed=true}):Results.NotFound();}
    catch(IOException){return Results.Problem("تعذر حذف صورة الإعلان من مجلد إعلانات الاستراحة.",statusCode:500);}
   });
   _server.MapGet("/api/announcement",(AnnouncementService announcements)=>
   {
    var item=announcements.Load();
    return Results.Ok(new{item.Title,item.Message,item.IsActive,item.Kind,item.UpdatedUtc});
   });
   _server.MapGet("/api/admin/announcement",(HttpContext c,AnnouncementService announcements)=>
    IsAdmin(c)?Results.Ok(announcements.Load()):Results.NotFound());
   _server.MapPost("/api/admin/announcement",(HttpContext c,RestHouseAnnouncement request,AnnouncementService announcements)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(request is null||request.Title?.Length>120||request.Message?.Length>1000||
       (request.Kind is not ("ad" or "notice"))||
       (request.IsActive&&string.IsNullOrWhiteSpace(request.Message)))
     return Results.BadRequest(new{error="تحقق من العنوان والرسالة ونوع الإعلان."});
    try{return Results.Ok(new{saved=true,announcement=announcements.Save(request)});}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
   });
   _server.MapGet("/api/admin/metadata/lookup",async (HttpContext c,string query,string? kind,MetadataLookupService lookup)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    try
    {
     var result=await lookup.SearchAsync(query,kind??"Movie",c.RequestAborted);
     if(result is null)return Results.NotFound(new{error="لم نعثر على بيانات مطابقة. جرّب كتابة الاسم الأصلي."});
     var posterPath=await lookup.DownloadPosterAsync(result.PosterUrl,posters,c.RequestAborted);
     return Results.Ok(new{result.Title,result.Description,result.Year,result.Genre,result.Kind,
      PosterPath=posterPath,result.Director,result.Rating,result.RuntimeMinutes,result.ReleaseDate,result.Provider});
    }
    catch(OperationCanceledException){return Results.StatusCode(499);}
    catch(HttpRequestException){return Results.Problem("تعذر الاتصال بمصدر البيانات. تأكد من توفر الإنترنت على جهاز الإدارة ثم أعد المحاولة.",statusCode:503);}
    catch(Exception ex){return Results.Problem("تعذر جلب بيانات الفيديو: "+ex.Message,statusCode:502);}
   });
   _server.MapPost("/api/messages",(CreateCustomerMessageRequest request,CustomerMessageService messages)=>{
    var allowed=new[]{"طلب مسلسل","الإبلاغ عن مشكلة","اقتراح","بلاغ شعار أو علامة تجارية","أخرى"};
    if(request is null||!allowed.Contains(request.Category)||string.IsNullOrWhiteSpace(request.Title)||request.Title.Trim().Length<3||request.Title.Length>120||string.IsNullOrWhiteSpace(request.Details)||request.Details.Trim().Length<8||request.Details.Length>2500||(request.Contact?.Length??0)>100||(request.ViewerId?.Length??0)>100)
     return Results.BadRequest(new{error="تحقق من نوع الرسالة والعنوان والتفاصيل. الحد الأقصى للتفاصيل 2500 حرف."});
    var id=messages.Create(request);
    return Results.Ok(new{sent=true,id,message="وصلت رسالتك إلى إدارة استراحة الأسطورة، شكراً لتواصلك."});
   });
   _server.MapGet("/api/admin/messages",(HttpContext c,CustomerMessageService messages)=>
    IsAdmin(c)?Results.Ok(messages.List()):Results.NotFound());
   _server.MapPost("/api/admin/messages/{id:long}/status",(HttpContext c,long id,CustomerMessageStatusRequest request,CustomerMessageService messages)=>{
    if(!IsAdmin(c))return Results.NotFound();
    if(request.Status is not ("جديد" or "قيد المتابعة" or "تمت المعالجة"))return Results.BadRequest(new{error="حالة الرسالة غير صالحة"});
    return messages.SetStatus(id,request.Status)?Results.Ok(new{updated=true}):Results.NotFound(new{error="الرسالة غير موجودة"});
   });
   _server.MapDelete("/api/admin/messages/{id:long}",(HttpContext c,long id,CustomerMessageService messages)=>{
    if(!IsAdmin(c))return Results.NotFound();
    return messages.Delete(id)?Results.Ok(new{deleted=true}):Results.NotFound(new{error="الرسالة غير موجودة"});
   });

   _server.MapGet("/api/admin/profile",(HttpContext c,RestHouseProfileService profile)=>
    IsAdmin(c)?Results.Ok(profile.Load()):Results.NotFound());
   _server.MapPost("/api/admin/profile",(HttpContext c,RestHouseProfile request,RestHouseProfileService profile)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(request is null||(request.LogoPath is not null&&!PosterExists(request.LogoPath,posters)))
     return Results.BadRequest(new{error="مسار شعار الشبكة غير صالح أو أن الصورة غير موجودة"});
    try{profile.Save(request);return Results.Ok(new{saved=true,profile=profile.Load()});}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
   });
   _server.MapPost("/api/admin/profile/logo",async (HttpContext c,RestHouseProfileService profile)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(c.Request.ContentLength is > 5_242_880)return Results.StatusCode(413);
    var contentType=c.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
    var ext=contentType switch{"image/jpeg"=>".jpg","image/png"=>".png","image/webp"=>".webp",_=>null};
    if(ext is null)return Results.BadRequest(new{error="الصيغ المسموحة JPG وPNG وWEBP فقط"});
    await using var ms=new MemoryStream();await c.Request.Body.CopyToAsync(ms);
    if(ms.Length==0||ms.Length>5_242_880)return Results.BadRequest(new{error="حجم الصورة يجب ألا يتجاوز 5 ميجابايت"});
    var bytes=ms.ToArray();
    var valid=contentType switch{
     "image/jpeg"=>bytes.Length>=3&&bytes[0]==0xFF&&bytes[1]==0xD8&&bytes[2]==0xFF,
     "image/png"=>bytes.Length>=8&&bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
     "image/webp"=>bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP",
     _=>false
    };
    if(!valid)return Results.BadRequest(new{error="محتوى الصورة لا يطابق صيغتها"});
    var fileName="brand-"+Guid.NewGuid().ToString("N")+ext;
    await File.WriteAllBytesAsync(Path.Combine(posters,fileName),bytes);
    var current=profile.Load();
    var updated=current with{LogoPath="/posters/"+fileName};
    try{profile.Save(updated);return Results.Ok(new{logoPath=updated.LogoPath});}
    catch{try{File.Delete(Path.Combine(posters,fileName));}catch{}throw;}
   });

   _server.MapGet("/api/catalog-categories",(CatalogCategoryService categories)=>Results.Ok(categories.List(publishedOnly:true)));
   _server.MapGet("/api/admin/catalog-categories",(HttpContext c,CatalogCategoryService categories)=>IsAdmin(c)?Results.Ok(categories.List()):Results.NotFound());
   _server.MapPost("/api/admin/catalog-categories",(HttpContext c,SaveCatalogCategoryRequest request,CatalogCategoryService categories)=>{
    if(!IsAdmin(c))return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.Title)||(request.Title?.Length??0)>100||(request.PosterPath?.Length??0)>240)return Results.BadRequest(new{error="اسم القسم أو الغلاف غير صالح"});
    if(!string.IsNullOrWhiteSpace(request.PosterPath)&&!PosterExists(request.PosterPath,posters))return Results.BadRequest(new{error="مسار غلاف القسم غير صالح أو أن ملف الصورة غير موجود"});
    if(request.ParentId is not null){var parent=categories.Get(request.ParentId);if(parent is null||parent.ParentId is not null||parent.ContentKind!="Group")return Results.BadRequest(new{error="اختر قسماً رئيسياً صحيحاً"});
     if(request.IsPublished&&!parent.IsPublished)return Results.BadRequest(new{error="يجب نشر القسم الرئيسي أولاً قبل نشر القسم الفرعي"});}
    try{categories.Save(request);return Results.Ok(new{saved=true});}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException){return Results.BadRequest(new{error=ex.Message});}
   });
   _server.MapDelete("/api/admin/catalog-categories/{id}",(HttpContext c,string id,CatalogCategoryService categories)=>{
    if(!IsAdmin(c))return Results.NotFound();try{categories.Delete(id);return Results.Ok(new{deleted=true});}catch(KeyNotFoundException ex){return Results.NotFound(new{error=ex.Message});}catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
   });
   _server.MapPost("/api/admin/catalog-categories/{id}/poster",async (HttpContext c,string id,CatalogCategoryService categories)=>{
    if(!IsAdmin(c))return Results.NotFound();if(categories.Get(id) is null)return Results.NotFound(new{error="القسم غير موجود"});
    if(c.Request.ContentLength is > 5_242_880)return Results.StatusCode(413);
    var type=c.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();var ext=type switch{"image/jpeg"=>".jpg","image/png"=>".png","image/webp"=>".webp",_=>null};
    if(ext is null)return Results.BadRequest(new{error="الصيغ المسموحة JPG وPNG وWEBP"});
    await using var ms=new MemoryStream();await c.Request.Body.CopyToAsync(ms);var bytes=ms.ToArray();
    var valid=type switch{"image/jpeg"=>bytes.Length>=3&&bytes[0]==255&&bytes[1]==216&&bytes[2]==255,"image/png"=>bytes.Length>=8&&bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"image/webp"=>bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP",_=>false};
    if(!valid||bytes.Length==0||bytes.Length>5_242_880)return Results.BadRequest(new{error="الصورة غير صالحة أو تجاوزت 5 ميجابايت"});
    var fileName="category-"+id+"-"+Guid.NewGuid().ToString("N")+ext;await File.WriteAllBytesAsync(Path.Combine(posters,fileName),bytes);
    var path="/posters/"+fileName;categories.SetPoster(id,path);return Results.Ok(new{posterPath=path});
   });

   _server.MapGet("/api/media-tools",(HttpContext c,MediaConversionService media)=>IsLocal(c)?Results.Ok(media.GetToolStatus()):Results.NotFound());
   _server.MapGet("/api/playback/{id}/tracks",async (string id,MediaConversionService media,LibraryService files,PublicCatalogAccessService publicCatalog)=>
   {
    if(files.Resolve(id) is null||!publicCatalog.IsVideoPublished(id))return Results.NotFound();
    return Results.Ok(await media.ProbeAsync(id));
   });
   _server.MapPost("/api/playback/{id}/prepare",async (string id,PreparePlaybackRequest request,MediaConversionService media,LibraryService files,PublicCatalogAccessService publicCatalog)=>
   {
    if(files.Resolve(id) is null||!publicCatalog.IsVideoPublished(id))return Results.NotFound();
    var result=await media.StartConversionAsync(id,request.AudioStreamIndex);
    return result.State=="failed"?Results.BadRequest(result):Results.Accepted("/api/playback/jobs/"+result.JobId,result);
   });
   _server.MapGet("/api/playback/jobs/{jobId}",(string jobId,MediaConversionService media,LibraryService files,PublicCatalogAccessService publicCatalog)=>
   {
    var videoId=media.GetSourceVideoId(jobId);
    if(videoId is null||files.Resolve(videoId) is null||!publicCatalog.IsVideoPublished(videoId))return Results.NotFound();
    return Results.Ok(media.GetJob(jobId));
   });
   _server.MapGet("/stream-cache/{jobId}.mp4",(string jobId,MediaConversionService media,LibraryService files,PublicCatalogAccessService publicCatalog)=>
   {
    var videoId=media.GetSourceVideoId(jobId);
    if(videoId is null||files.Resolve(videoId) is null||!publicCatalog.IsVideoPublished(videoId))return Results.NotFound();
    var path=media.ResolveCachedFile(jobId);
    return path is null?Results.NotFound():Results.File(path,"video/mp4",enableRangeProcessing:true);
   });

   _server.MapGet("/api/media-libraries",(string? kind,MultiMediaLibraryService media)=>
   {
    if(kind is not null && kind is not ("Audio" or "Books" or "Apps")) return Results.BadRequest(new{error="نوع المكتبة غير صالح"});
    return Results.Ok(media.List(kind));
   });
   _server.MapGet("/api/admin/media-libraries/roots",(HttpContext c,string? kind,MultiMediaLibraryService media)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(kind is not null && kind is not ("Audio" or "Books" or "Apps"))return Results.BadRequest(new{error="نوع المكتبة غير صالح"});
    return Results.Ok(media.ListRoots(kind));
   });
   _server.MapPost("/api/admin/media-libraries/roots",(HttpContext c,AddMediaLibraryRootRequest request,MultiMediaLibraryService media)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(request is null || request.Kind is not ("Audio" or "Books" or "Apps") || string.IsNullOrWhiteSpace(request.FolderPath))
     return Results.BadRequest(new{error="حدد نوع المكتبة ومسار المجلد"});
    try{return Results.Ok(media.AddRoot(request.Kind,request.FolderPath));}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(DirectoryNotFoundException){return Results.BadRequest(new{error="المجلد المحدد غير موجود أو غير متاح"});}
    catch(IOException){return Results.Problem("تعذر تسجيل المجلد المحدد.",statusCode:500);}
   });
   _server.MapDelete("/api/admin/media-libraries/roots/{id}",(HttpContext c,string id,MultiMediaLibraryService media)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(string.IsNullOrWhiteSpace(id)||id.Length>64)return Results.BadRequest(new{error="معرّف المجلد غير صالح"});
    return media.RemoveRoot(id)?Results.Ok(new{removed=true,filesPreserved=true}):Results.BadRequest(new{error="لا يمكن إزالة مجلد المكتبة الأساسي أو أن المجلد غير مسجل"});
   });
   _server.MapGet("/api/admin/storage-summary",(HttpContext c,MultiMediaLibraryService media)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    return Results.Ok(media.GetStorageSummary());
   });
   _server.MapPost("/api/admin/media-libraries/scan",(HttpContext c,ScanMediaLibraryRequest request,MultiMediaLibraryService media)=>
   {
    if(!IsAdmin(c))return Results.NotFound();
    if(request is null || request.Kind is not ("Audio" or "Books" or "Apps"))return Results.BadRequest(new{error="اختر مكتبة الصوتيات أو الكتب أو التطبيقات"});
    try{return Results.Ok(new{scanned=true,kind=request.Kind,count=media.Scan(request.Kind),items=media.List(request.Kind)});}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(IOException){return Results.Problem("تعذر فحص مجلد المكتبة. تحقق من صلاحيات الوصول إلى الملفات.",statusCode:500);}
   });
   _server.MapGet("/api/downloads/{clientId}",(string clientId,DownloadProgressService downloads)=>
   {
    if(string.IsNullOrWhiteSpace(clientId)||clientId.Length>80||!Regex.IsMatch(clientId,@"^[a-zA-Z0-9_-]+$"))return Results.BadRequest(new{error="معرّف العميل غير صالح"});
    return Results.Ok(downloads.List(clientId));
   });
   _server.MapDelete("/api/downloads/{clientId}/{downloadId}",(string clientId,string downloadId,DownloadProgressService downloads)=>
   {
    if(string.IsNullOrWhiteSpace(clientId)||clientId.Length>80||!Regex.IsMatch(clientId,@"^[a-zA-Z0-9_-]+$")||string.IsNullOrWhiteSpace(downloadId)||downloadId.Length>80||!Regex.IsMatch(downloadId,@"^[a-zA-Z0-9_-]+$"))return Results.BadRequest(new{error="معرّف التنزيل غير صالح"});
    return downloads.Remove(clientId,downloadId)?Results.Ok(new{removed=true}):Results.NotFound();
   });
   _server.MapGet("/api/media-library/{id}/download",async (HttpContext context,string id,string clientId,string downloadId,MultiMediaLibraryService media,DownloadProgressService downloads,DownloadSpeedLimitService speedLimit)=>
   {
    if(string.IsNullOrWhiteSpace(clientId)||clientId.Length>80||!Regex.IsMatch(clientId,@"^[a-zA-Z0-9_-]+$")||string.IsNullOrWhiteSpace(downloadId)||downloadId.Length>80||!Regex.IsMatch(downloadId,@"^[a-zA-Z0-9_-]+$"))
     return Results.BadRequest(new{error="معرّف التنزيل غير صالح"});
    var path=media.ResolveById(id);
    if(path is null)return Results.NotFound();
    var info=new FileInfo(path);
    if(!info.Exists||info.Length<=0)return Results.NotFound();
    long start=0,end=info.Length-1;
    var entityTag=$"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
    var lastModified=info.LastWriteTimeUtc.ToString("R");
    var range=context.Request.Headers["Range"].ToString();
    var ifRange=context.Request.Headers["If-Range"].ToString();
    if(!string.IsNullOrWhiteSpace(ifRange)&&!string.Equals(ifRange,entityTag,StringComparison.Ordinal)&&!string.Equals(ifRange,lastModified,StringComparison.Ordinal))range="";
    var partial=false;
    if(!string.IsNullOrWhiteSpace(range))
    {
     if(!range.StartsWith("bytes=",StringComparison.OrdinalIgnoreCase)||range.Contains(','))
     {
      context.Response.Headers["Content-Range"]=$"bytes */{info.Length}";
      return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
     }
     var parts=range[6..].Split('-',2);
     if(parts.Length!=2)
     {
      context.Response.Headers["Content-Range"]=$"bytes */{info.Length}";
      return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
     }
     if(parts[0].Length==0)
     {
      if(!long.TryParse(parts[1],out var suffix)||suffix<=0)
      {
       context.Response.Headers["Content-Range"]=$"bytes */{info.Length}";
       return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
      }
      start=Math.Max(0,info.Length-suffix);
     }
     else if(!long.TryParse(parts[0],out start)||start<0||start>=info.Length)
     {
      context.Response.Headers["Content-Range"]=$"bytes */{info.Length}";
      return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
     }
     if(parts[1].Length>0&&(!long.TryParse(parts[1],out end)||end<start))
     {
      context.Response.Headers["Content-Range"]=$"bytes */{info.Length}";
      return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
     }
     end=Math.Min(end,info.Length-1);
     partial=true;
    }
    var length=end-start+1;
    var extension=Path.GetExtension(path).ToLowerInvariant();
    var contentType=extension switch
    {
     ".mp3"=>"audio/mpeg",".m4a"=>"audio/mp4",".aac"=>"audio/aac",".ogg"=>"audio/ogg",
     ".opus"=>"audio/opus",".wav"=>"audio/wav",".flac"=>"audio/flac",
     ".pdf"=>"application/pdf",".epub"=>"application/epub+zip",".txt"=>"text/plain",
     ".cbz"=>"application/vnd.comicbook+zip",".cbr"=>"application/vnd.comicbook-rar",
     ".apk" or ".xapk" or ".apks"=>"application/vnd.android.package-archive",
     _=>"application/octet-stream"
    };
    context.Response.StatusCode=partial?StatusCodes.Status206PartialContent:StatusCodes.Status200OK;
    context.Response.ContentType=contentType;
    context.Response.ContentLength=length;
    context.Response.Headers["Accept-Ranges"]="bytes";
    context.Response.Headers["Content-Disposition"]=new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment"){FileNameStar=info.Name}.ToString();
    context.Response.Headers["Last-Modified"]=lastModified;
    context.Response.Headers["ETag"]=entityTag;
    if(partial)context.Response.Headers["Content-Range"]=$"bytes {start}-{end}/{info.Length}";
    downloads.Begin(clientId,downloadId,id,info.Name,info.Length,start);
    try
    {
     await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,64*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
     stream.Seek(start,SeekOrigin.Begin);
     var buffer=new byte[64*1024];
     long sent=0;
     while(sent<length)
     {
      var wanted=(int)Math.Min(buffer.Length,length-sent);
      var read=await stream.ReadAsync(buffer.AsMemory(0,wanted),context.RequestAborted);
      if(read<=0)break;
      await speedLimit.WaitAsync(read,context.RequestAborted);
      await context.Response.Body.WriteAsync(buffer.AsMemory(0,read),context.RequestAborted);
      sent+=read;
      downloads.Report(clientId,downloadId,start+sent);
     }
     if(sent==length)downloads.Complete(clientId,downloadId);
     else downloads.Interrupt(clientId,downloadId);
    }
    catch(OperationCanceledException){downloads.Interrupt(clientId,downloadId);}
    catch(IOException){downloads.Interrupt(clientId,downloadId);throw;}
    return Results.Empty;
   });
   _server.MapGet("/api/media-library/{id}",(string id,MultiMediaLibraryService media)=>
   {
    if(string.IsNullOrWhiteSpace(id)||id.Length>64)return Results.NotFound();
    var path=media.ResolveById(id);
    if(path is null)return Results.NotFound();
    var extension=Path.GetExtension(path).ToLowerInvariant();
    var contentType=extension switch
    {
     ".mp3"=>"audio/mpeg",".m4a"=>"audio/mp4",".aac"=>"audio/aac",".ogg"=>"audio/ogg",
     ".opus"=>"audio/opus",".wav"=>"audio/wav",".flac"=>"audio/flac",
     ".pdf"=>"application/pdf",".epub"=>"application/epub+zip",".txt"=>"text/plain",
     ".cbz"=>"application/vnd.comicbook+zip",".cbr"=>"application/vnd.comicbook-rar",
     ".apk" or ".xapk" or ".apks"=>"application/vnd.android.package-archive",
     _=>"application/octet-stream"
    };
    return Results.File(path,contentType,enableRangeProcessing:contentType.StartsWith("audio/",StringComparison.Ordinal));
   });
   _server.MapGet("/api/library",(HttpContext c,LibraryService svc,VideoMetadataService meta,SeriesCatalogService catalog,CatalogCategoryService categories,PublicCatalogAccessService publicCatalog,WatchProgressService watchProgress)=>
   {
    var isLocal=IsLocal(c);
    IReadOnlySet<string>? visibleVideoIds=isLocal?null:publicCatalog.GetPublishedVideoIds();
    var metadataById=meta.List().ToDictionary(x=>x.VideoId);
    var viewCounts=watchProgress.GetViewCounts();
    var categoryById=categories.List().ToDictionary(x=>x.CategoryId,x=>x.Title,StringComparer.OrdinalIgnoreCase);
    var posterLookup=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    try
    {
     foreach(var posterFile in Directory.EnumerateFiles(posters,"*",SearchOption.AllDirectories))
     {
      if(Path.GetExtension(posterFile).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".webp")) continue;
      var key=Path.GetFileNameWithoutExtension(posterFile);
      if(!posterLookup.ContainsKey(key)) posterLookup[key]=posterFile;
     }
    }
    catch { }
    var result=svc.List().Where(v=>isLocal||visibleVideoIds!.Contains(v.Id)).Select(v=>
    {
     metadataById.TryGetValue(v.Id,out var m);
     var resolvedVideo=svc.Resolve(v.Id);
     var addedUtc=resolvedVideo is null?null:File.GetCreationTimeUtc(resolvedVideo.FullPath).ToString("O");
     var folderPath=resolvedVideo is null?string.Empty:(Path.GetDirectoryName(resolvedVideo.FullPath)??string.Empty);
     var folderName=string.IsNullOrWhiteSpace(folderPath)?"مجلد غير معروف":Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
     if(string.IsNullOrWhiteSpace(folderName))folderName=folderPath;
     var folderId=string.IsNullOrWhiteSpace(folderPath)?"root":Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(folderPath)))[..16];
     var posterPath=m?.PosterPath;
     if(string.IsNullOrWhiteSpace(posterPath))
     {
      var baseName=Path.GetFileNameWithoutExtension(v.FileName);
      if(posterLookup.TryGetValue(baseName,out var posterFile))
      {
       var relative=Path.GetRelativePath(posters,posterFile).Replace('\\','/');
       posterPath="/posters/"+string.Join("/",relative.Split('/').Select(Uri.EscapeDataString));
      }
     }
     var placement=catalog.GetVideoPlacement(v.Id);
     return new{id=v.Id,title=m?.Title??v.Title,fileName=v.FileName,url=v.Url,sizeBytes=v.SizeBytes,addedUtc,folderId,folderName,folderPath=isLocal?folderPath:null,description=m?.Description??"",year=m?.Year,genre=m?.Genre??"",kind=m?.Kind??"Movie",posterPath,director=m?.Director,cast=m?.Cast,rating=m?.Rating,runtimeMinutes=m?.RuntimeMinutes,releaseDate=m?.ReleaseDate,tags=m?.Tags,categoryId=m?.CategoryId,categoryTitle=m is not null&&m.CategoryId is not null&&categoryById.TryGetValue(m.CategoryId,out var categoryTitle)?categoryTitle:null,linkedSeriesId=placement?.SeriesId,linkedSeasonId=placement?.SeasonId,linkedEpisodeNumber=placement?.EpisodeNumber,linkedEpisodeTitle=placement?.Title,viewCount=viewCounts.TryGetValue(v.Id,out var viewCount)?viewCount:0,metadataSaved=m is not null,section=m?.Section??"عام",isPublished=m?.IsPublished??false};
    });
    return Results.Ok(result);
   });
   _server.MapGet("/api/series",(HttpContext c,SeriesCatalogService series,LibraryService files,VideoMetadataService meta,PublicCatalogAccessService publicCatalog)=>
   {
    IReadOnlySet<string>? allowedSeriesIds=IsLocal(c)?null:publicCatalog.GetPublishedSeriesIds();
    return Results.Ok(series.GetCatalog(files,meta,allowedSeriesIds,IsLocal(c)?null:publicCatalog.GetPublishedVideoIds()));
   });
   _server.MapGet("/api/admin/series",(HttpContext c,SeriesCatalogService series)=>{ if(!IsAdmin(c)) return Results.NotFound(); return Results.Ok(series.ListSeries()); });
   _server.MapDelete("/api/admin/series/{seriesId}",(HttpContext c,string seriesId,SeriesCatalogService series)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(seriesId)||seriesId.Length>80) return Results.BadRequest(new{error="معرّف المسلسل غير صالح"});
    try { series.DeleteSeries(seriesId); return Results.Ok(new{deleted=true,message="تم حذف بيانات المسلسل ومواسمه وروابط حلقاته دون حذف ملفات الفيديو الأصلية"}); }
    catch(KeyNotFoundException ex) { return Results.NotFound(new{error=ex.Message}); }
   });
   _server.MapDelete("/api/admin/seasons/{seasonId:long}",(HttpContext c,long seasonId,SeriesCatalogService series)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    try { series.DeleteSeason(seasonId); return Results.Ok(new{deleted=true,message="تم حذف الموسم وروابط حلقاته دون حذف ملفات الفيديو"}); }
    catch(KeyNotFoundException ex) { return Results.NotFound(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/series",(HttpContext c,SaveSeriesRequest request,SeriesCatalogService series,CatalogCategoryService categories)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.SeriesId)||request.SeriesId.Length>80||string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>160||(request.Description?.Length??0)>2000||(request.Genre?.Length??0)>80||(request.Year is not null&&(request.Year<1888||request.Year>DateTime.UtcNow.Year+5))||(request.PosterPath?.Length??0)>200||(request.FolderPath?.Length??0)>1000||(request.FolderPath is not null&&!Directory.Exists(request.FolderPath))||request.OrganizationType is not ("None" or "Seasons" or "Parts")) return Results.BadRequest(new{error="تحقق من بيانات المسلسل وطريقة تنظيمه"});
    if(request.OrganizationType=="None"&&series.ListSeasons(request.SeriesId).Count>0)return Results.BadRequest(new{error="هذا المسلسل يحتوي على مواسم أو أجزاء مسجلة. لا يمكن تحويله إلى تنظيم مباشر قبل نقل محتواها."});
    if(request.IsPublished)
    {
     var previous=series.GetSeries(request.SeriesId);
     var poster=request.PosterPath??previous?.PosterPath;
     var category=string.IsNullOrWhiteSpace(request.CategoryId)?null:categories.Get(request.CategoryId);
     var parent=category?.ParentId is null?null:categories.Get(category.ParentId);
     if(!string.IsNullOrWhiteSpace(poster)&&!PosterExists(poster,posters))return Results.BadRequest(new{error="مسار غلاف المسلسل غير صالح أو أن ملف الصورة غير موجود"});
     if(category is null||category.ContentKind!="Series"||!category.IsPublished||parent is null||!parent.IsPublished)return Results.BadRequest(new{error="اختر تصنيف مسلسلات منشوراً تابعاً لقسم رئيسي منشور"});
    }
    series.SaveSeries(request); return Results.Ok(new{saved=true});
   });
   _server.MapGet("/api/admin/seasons",(HttpContext c,string seriesId,SeriesCatalogService series)=>{ if(!IsAdmin(c)) return Results.NotFound(); return Results.Ok(series.ListSeasons(seriesId)); });
   _server.MapPost("/api/admin/seasons",(HttpContext c,SaveSeasonRequest request,SeriesCatalogService series)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.SeriesId)||request.SeasonNumber<1||request.SeasonNumber>500||(request.Title?.Length??0)>120||(request.FolderPath?.Length??0)>1000||(request.FolderPath is not null&&!Directory.Exists(request.FolderPath))) return Results.BadRequest(new{error="بيانات الموسم أو الجزء غير صحيحة أو مجلده غير موجود"});
    var ownerSeries=series.GetSeries(request.SeriesId);
    if(ownerSeries is null)return Results.NotFound(new{error="المسلسل غير موجود"});
    if(ownerSeries.OrganizationType=="None")return Results.BadRequest(new{error="هذا البرنامج مضبوط على فيديوهات مباشرة ولا يستخدم مواسم أو أجزاء."});
    try { var id=series.AddSeason(request); return Results.Ok(new{seasonId=id}); } catch(Microsoft.Data.Sqlite.SqliteException) { return Results.Conflict(new{error="المسلسل غير موجود أو رقم الموسم مكرر"}); }
   });
   _server.MapPost("/api/admin/seasons/{seasonId:long}/folder",(HttpContext c,long seasonId,UpdateSeasonFolderRequest request,SeriesCatalogService series)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if((request.FolderPath?.Length??0)>1000||(request.FolderPath is not null&&!Directory.Exists(request.FolderPath))) return Results.BadRequest(new{error="مجلد الموسم غير موجود"});
    try { series.UpdateSeasonFolder(seasonId,request.FolderPath); return Results.Ok(new{saved=true}); }
    catch(KeyNotFoundException) { return Results.NotFound(new{error="الموسم غير موجود"}); }
   });
   _server.MapGet("/api/admin/seasons/{seasonId:long}/preview",(HttpContext c,long seasonId,SeriesCatalogService series,LibraryService files)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    var season=series.GetSeason(seasonId);
    if(season is null) return Results.NotFound(new{error="الموسم غير موجود"});
    if(string.IsNullOrWhiteSpace(season.FolderPath)||!Directory.Exists(season.FolderPath)) return Results.BadRequest(new{error="حدد مجلد الموسم واحفظ مساره أولاً"});
    try
    {
     var root=Path.GetFullPath(season.FolderPath);
     var linked=series.ListEpisodes(seasonId);
     var usedNumbers=linked.Select(x=>x.EpisodeNumber).ToHashSet();
     var knownFiles=files.List().Select(video=>(video,resolved:files.Resolve(video.Id)))
      .Where(x=>x.resolved is not null)
      .GroupBy(x=>Path.GetFullPath(x.resolved!.FullPath),StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group=>group.Key,group=>group.First().video,StringComparer.OrdinalIgnoreCase);
     var candidates=files.PreviewFolder(root).Select(file=>{
      var path=Path.GetFullPath(file.FullPath);
      knownFiles.TryGetValue(path,out var known);
      var number=ParseEpisodeNumber(file.FileName);
      return new {
       videoId=known?.Id??"",
       filePath=path,
       fileName=file.FileName,
       title=known?.Title??Path.GetFileNameWithoutExtension(file.FileName),
       episodeNumber=number,
       isAlreadyLinked=known is not null&&series.GetVideoPlacement(known.Id) is not null,
       isNumberUsed=number is int n&&usedNumbers.Contains(n)
      };
     }).ToArray();
     var duplicateNumbers=candidates.Where(x=>x.episodeNumber.HasValue)
      .GroupBy(x=>x.episodeNumber!.Value).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
     return Results.Ok(candidates.Select(x=>new {
      x.videoId,x.filePath,x.fileName,x.title,x.episodeNumber,x.isAlreadyLinked,
      isNumberUsed=x.isNumberUsed||(x.episodeNumber.HasValue&&duplicateNumbers.Contains(x.episodeNumber.Value))
     }));
    }
    catch(UnauthorizedAccessException){return Results.BadRequest(new{error="لا توجد صلاحية لقراءة بعض محتويات المجلد المحدد."});}
    catch(IOException ex){return Results.BadRequest(new{error="تعذر فحص المجلد: "+ex.Message});}
   });
   _server.MapPost("/api/admin/seasons/{seasonId:long}/link-detected",(HttpContext c,long seasonId,LinkDetectedEpisodesRequest request,SeriesCatalogService series,LibraryService files)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(request?.Episodes is null||request.Episodes.Count>1000) return Results.BadRequest(new{error="قائمة الحلقات غير صالحة"});
    var season=series.GetSeason(seasonId);
    if(season is null) return Results.NotFound(new{error="الموسم غير موجود"});
    if(string.IsNullOrWhiteSpace(season.FolderPath)||!Directory.Exists(season.FolderPath)) return Results.BadRequest(new{error="مجلد الموسم غير متاح"});
    var root=Path.GetFullPath(season.FolderPath).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
    var selected=request.Episodes.Where(x=>x.Selected).ToArray();
    if(selected.Any(x=>x.EpisodeNumber<1||x.EpisodeNumber>10000||string.IsNullOrWhiteSpace(x.FilePath)||x.FilePath.Length>2000))
     return Results.BadRequest(new{error="تأكد من رقم الحلقة ومسار كل ملف محدد."});
    var paths=new List<string>();
    foreach(var item in selected)
    {
     string full;
     try{full=Path.GetFullPath(item.FilePath!);}
     catch{return Results.BadRequest(new{error="يوجد مسار ملف غير صالح."});}
     if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!File.Exists(full))
      return Results.BadRequest(new{error="أحد الملفات المحددة لم يعد موجوداً داخل مجلد الموسم."});
     paths.Add(full);
    }
    if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Count)
     return Results.BadRequest(new{error="تم تحديد الملف نفسه أكثر من مرة."});
    var existing=series.ListEpisodes(seasonId);
    var usedIds=existing.Select(x=>x.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var usedNumbers=existing.Select(x=>x.EpisodeNumber).ToHashSet();
    var duplicateNumbers=selected.GroupBy(x=>x.EpisodeNumber).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
    if(selected.Any(x=>duplicateNumbers.Contains(x.EpisodeNumber)||usedNumbers.Contains(x.EpisodeNumber)))
     return Results.Conflict(new{error="يوجد تعارض في أرقام الحلقات المحددة. عدّل الأرقام في المعاينة قبل الاعتماد."});
    try
    {
     if(paths.Count>0) files.RegisterSelectedFiles(season.FolderPath!,paths);
     var idByPath=files.List().Select(video=>(video,resolved:files.Resolve(video.Id)))
      .Where(x=>x.resolved is not null)
      .GroupBy(x=>Path.GetFullPath(x.resolved!.FullPath),StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group=>group.Key,group=>group.First().video.Id,StringComparer.OrdinalIgnoreCase);
     var linkedCount=0; var skipped=0;
     foreach(var item in selected)
     {
      if(!idByPath.TryGetValue(Path.GetFullPath(item.FilePath!),out var videoId)||usedIds.Contains(videoId))
      {skipped++;continue;}
      try
      {
       series.AddEpisode(new SaveEpisodeRequest(seasonId,videoId,item.EpisodeNumber,item.Title));
       usedIds.Add(videoId); usedNumbers.Add(item.EpisodeNumber); linkedCount++;
      }
      catch(Microsoft.Data.Sqlite.SqliteException){skipped++;}
     }
     return Results.Ok(new{linkedCount,skipped});
    }
    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
    {return Results.BadRequest(new{error="تعذر تسجيل الملفات المحددة وربطها: "+ex.Message});}
   });
   _server.MapGet("/api/admin/preview-folder",(HttpContext c,string folderPath,LibraryService files)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(folderPath)||folderPath.Length>1000||!Directory.Exists(folderPath)) return Results.BadRequest(new{error="حدد مجلداً موجوداً ويمكن الوصول إليه"});
    try { return Results.Ok(files.PreviewFolder(folderPath)); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapGet("/api/admin/library/folders",(HttpContext c,LibraryService files)=>
   {
    if(!IsLocal(c)) return Results.NotFound();
    return Results.Ok(files.ListFolders());
   });
   _server.MapPost("/api/admin/library/folders",(HttpContext c,CreateLibraryFolderRequest request,LibraryService files)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if((request.ParentPath?.Length??0)>2000||string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>80)
     return Results.BadRequest(new{error="بيانات المجلد غير صحيحة"});
    try
    {
     var path=files.CreateFolder(request.ParentPath,request.Name);
     return Results.Ok(new{created=true,fullPath=path,message="تم إنشاء المجلد داخل مكتبة الفيديو."});
    }
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(DirectoryNotFoundException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(IOException ex){return Results.BadRequest(new{error=ex.Message});}
   });
   _server.MapPost("/api/admin/register-files",(HttpContext c,RegisterFilesRequest request,LibraryService files)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if(request.FilePaths is null||request.FilePaths.Count<1||request.FilePaths.Count>1000||request.FilePaths.Any(path=>string.IsNullOrWhiteSpace(path)||path.Length>2000))
     return Results.BadRequest(new{error="اختر ملفات فيديو صحيحة، بحد أقصى 1000 ملف في المرة الواحدة"});
    try { var count=files.RegisterFiles(request.FilePaths); return Results.Ok(new{registered=true,videoCount=count,message="تمت إضافة الملفات المحددة إلى المكتبة"}); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/register-selected-files",(HttpContext c,RegisterSelectedFilesRequest request,LibraryService files)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.FolderPath)||request.FolderPath.Length>1000||!Directory.Exists(request.FolderPath)||request.FilePaths is null||request.FilePaths.Count>5000) return Results.BadRequest(new{error="بيانات المجلد أو قائمة الملفات غير صحيحة"});
    try { var count=files.RegisterSelectedFiles(request.FolderPath,request.FilePaths); return Results.Ok(new{registered=true,videoCount=count}); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/library/unregister-folder",(HttpContext c,UnregisterLibraryFolderRequest request,LibraryService files)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if(request is null||string.IsNullOrWhiteSpace(request.FolderPath)||request.FolderPath.Length>2000)
     return Results.BadRequest(new{error="مسار التخزين غير صالح"});
    try
    {
     var count=files.UnregisterFolder(request.FolderPath);
     return Results.Ok(new{unregistered=true,videoCount=count,message="تم فك تسجيل مسار التخزين من المكتبة فقط. لم يتم حذف أي ملف من القرص أو بيانات الفيديو."});
    }
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(KeyNotFoundException ex){return Results.NotFound(new{error=ex.Message});}
   });
   _server.MapPost("/api/admin/scan-folder",(HttpContext c,ScanFolderRequest request,LibraryService files)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.FolderPath)||(request.FolderPath.Length>1000)||!Directory.Exists(request.FolderPath)) return Results.BadRequest(new{error="حدد مجلداً موجوداً ويمكن الوصول إليه"});
    try { var count=files.RegisterFolder(request.FolderPath); return Results.Ok(new{registered=true,videoCount=count}); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/episodes",(HttpContext c,SaveEpisodeRequest request,LibraryService files,SeriesCatalogService series)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(request.SeasonId<1||request.EpisodeNumber<1||request.EpisodeNumber>10000||string.IsNullOrWhiteSpace(request.VideoId)||files.Resolve(request.VideoId) is null||(request.Title?.Length??0)>160) return Results.BadRequest(new{error="بيانات الحلقة غير صحيحة أو ملف الفيديو غير موجود"});
    try { var id=series.AddEpisode(request); return Results.Ok(new{episodeId=id}); } catch(Microsoft.Data.Sqlite.SqliteException) { return Results.Conflict(new{error="الموسم غير موجود أو الملف مربوط بحلقة أخرى أو رقم الحلقة مكرر"}); }
   });
   _server.MapGet("/api/admin/seasons/{seasonId:long}/episodes",(HttpContext c,long seasonId,SeriesCatalogService series)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(series.GetSeason(seasonId) is null) return Results.NotFound(new{error="الموسم غير موجود"});
    return Results.Ok(series.ListEpisodes(seasonId));
   });
   _server.MapPut("/api/admin/episodes/{episodeId:long}",(HttpContext c,long episodeId,UpdateEpisodeRequest request,SeriesCatalogService series)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(request.EpisodeNumber<1||request.EpisodeNumber>10000||(request.Title?.Length??0)>160)
     return Results.BadRequest(new{error="رقم الحلقة أو اسمها غير صالح"});
    try { series.UpdateEpisode(episodeId,request.EpisodeNumber,request.Title); return Results.Ok(new{updated=true}); }
    catch(KeyNotFoundException ex) { return Results.NotFound(new{error=ex.Message}); }
    catch(Microsoft.Data.Sqlite.SqliteException) { return Results.Conflict(new{error="رقم الحلقة مستخدم بالفعل في هذا الموسم"}); }
   });
   _server.MapDelete("/api/admin/episodes/{episodeId:long}",(HttpContext c,long episodeId,SeriesCatalogService series)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    try { var videoId=series.UnlinkEpisode(episodeId); return Results.Ok(new{unlinked=true,videoId,message="تم فك ربط الحلقة فقط، ولم يتم حذف ملف الفيديو"}); }
    catch(KeyNotFoundException ex) { return Results.NotFound(new{error=ex.Message}); }
   });
   _server.MapGet("/api/admin/missing-episodes",(HttpContext c,SeriesCatalogService series,LibraryService files)=>
    IsAdmin(c)?Results.Ok(series.ListMissingEpisodes(files)):Results.NotFound());
   _server.MapPost("/api/admin/episodes/{episodeId:long}/relink",(HttpContext c,long episodeId,RelinkEpisodeRequest request,SeriesCatalogService series,LibraryService files,WatchProgressService progress)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.VideoId)||request.VideoId.Length!=24||!request.VideoId.All(Uri.IsHexDigit)||files.Resolve(request.VideoId) is null)
     return Results.BadRequest(new{error="ملف الفيديو الجديد غير موجود في المكتبة"});
    try { var oldVideoId=series.UpdateEpisodeVideo(episodeId,request.VideoId); progress.MoveVideoProgress(oldVideoId,request.VideoId); return Results.Ok(new{relinked=true,progressPreserved=true}); }
    catch(KeyNotFoundException) { return Results.NotFound(new{error="الحلقة غير موجودة"}); }
    catch(Microsoft.Data.Sqlite.SqliteException) { return Results.Conflict(new{error="هذا الملف مربوط بحلقة أخرى بالفعل"}); }
   });
   _server.MapGet("/api/admin/metadata",(HttpContext c,VideoMetadataService meta)=>IsAdmin(c)?Results.Ok(meta.List()):Results.NotFound());
   _server.MapPost("/api/admin/metadata",(HttpContext c,SaveVideoMetadataRequest request,LibraryService librarySvc,VideoMetadataService meta,CatalogCategoryService categories,SeriesCatalogService series)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(request.VideoId)||request.VideoId.Length!=24||!request.VideoId.All(Uri.IsHexDigit)||librarySvc.Resolve(request.VideoId) is null) return Results.BadRequest(new{error="الفيديو غير موجود"});
    if(string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>160||(request.Description?.Length??0)>2000||(request.Genre?.Length??0)>80||(request.Year is not null&&(request.Year<1888||request.Year>DateTime.UtcNow.Year+5))||(request.PosterPath?.Length??0)>200||(request.Director?.Length??0)>160||(request.Cast?.Length??0)>1000||(request.Tags?.Length??0)>500||(request.Rating is <0 or >10)||(request.RuntimeMinutes is <1 or >1000)||(request.ReleaseDate?.Length??0)>10) return Results.BadRequest(new{error="تحقق من البيانات المدخلة"});
    if(request.IsPublished)
    {
     var previous=meta.Get(request.VideoId);
     var poster=request.PosterPath??previous?.PosterPath;
     var category=string.IsNullOrWhiteSpace(request.CategoryId)?null:categories.Get(request.CategoryId);
     var parent=category?.ParentId is null?null:categories.Get(category.ParentId);
     var expectedKind=request.Kind=="Series"?"Series":"Movie";
     if(!string.IsNullOrWhiteSpace(poster)&&!PosterExists(poster,posters))return Results.BadRequest(new{error="مسار غلاف الفيديو غير صالح أو أن ملف الصورة غير موجود"});
     if(category is null||(category.ContentKind!=expectedKind&&category.ContentKind!="Other")||!category.IsPublished||parent is null||!parent.IsPublished)return Results.BadRequest(new{error="اختر تصنيفاً فرعياً منشوراً من قسم رئيسي منشور"});
    }
    if(!string.IsNullOrWhiteSpace(request.SeriesId))
    {
     var selectedSeries=series.GetSeries(request.SeriesId);
     if(selectedSeries is null||request.Kind!="Series")return Results.BadRequest(new{error="اختر مسلسلاً صحيحاً لربط هذا الفيديو به."});
     if(!string.Equals(selectedSeries.CategoryId,request.CategoryId,StringComparison.OrdinalIgnoreCase))
      return Results.BadRequest(new{error="قسم الفيديو يجب أن يطابق قسم المسلسل. تم اختيار قسم المسلسل تلقائياً عند تحديده."});
     if(request.SeasonId is not null)
     {
      var selectedSeason=series.GetSeason(request.SeasonId.Value);
      if(selectedSeason is null||selectedSeason.SeriesId!=request.SeriesId)return Results.BadRequest(new{error="الموسم أو الجزء المختار لا يتبع المسلسل المحدد."});
      if(request.EpisodeNumber is null or <1 or >10000)return Results.BadRequest(new{error="أدخل رقم الحلقة أو الفيديو داخل الموسم/الجزء."});
     }
    }
    else if(request.SeasonId is not null)return Results.BadRequest(new{error="اختر المسلسل قبل اختيار الموسم أو الجزء."});
    try
    {
     meta.Save(request);
     series.SetVideoPlacement(request.SeriesId,request.SeasonId,request.VideoId,request.EpisodeNumber,request.EpisodeTitle??request.Title);
     return Results.Ok(new{saved=true});
    }
    catch(Microsoft.Data.Sqlite.SqliteException){return Results.Conflict(new{error="تعذر ربط الفيديو: قد يكون رقم الحلقة مستخدماً بالفعل في هذا الموسم أو الجزء."});}
    catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or KeyNotFoundException){return Results.BadRequest(new{error=ex.Message});}
   });
   _server.MapPost("/api/admin/poster/{videoId}",async (HttpContext c,string videoId,LibraryService librarySvc,VideoMetadataService meta)=>{
    if(!IsAdmin(c)) return Results.NotFound();
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
    meta.Save(new SaveVideoMetadataRequest(videoId,old?.Title??(librarySvc.List().First(x=>x.Id==videoId).Title),old?.Description??"",old?.Year,old?.Genre??"",old?.Kind??"Movie","/posters/"+fileName,old?.Director,old?.Cast,old?.Rating,old?.RuntimeMinutes,old?.ReleaseDate,old?.Tags,old?.Section??"عام",old?.IsPublished??false,old?.CategoryId));
    return Results.Ok(new{posterPath="/posters/"+fileName});
   });
   _server.MapPost("/api/admin/episode-poster/{episodeId:long}",async (HttpContext c,long episodeId,SeriesCatalogService series)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if(series.GetEpisodePoster(episodeId) is null && !series.ListSeries().Any(s=>series.GetCatalog(
       c.RequestServices.GetRequiredService<LibraryService>(),
       c.RequestServices.GetRequiredService<VideoMetadataService>()) is not null))
    {
     // The update below is the authoritative existence check; a null poster is valid.
    }
    if(c.Request.ContentLength is > 5_242_880) return Results.StatusCode(413);
    var type=c.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
    var ext=type switch {"image/jpeg"=>".jpg","image/png"=>".png","image/webp"=>".webp",_=>null};
    if(ext is null) return Results.BadRequest(new{error="الصيغ المسموحة JPG وPNG وWEBP فقط"});
    await using var ms=new MemoryStream(); await c.Request.Body.CopyToAsync(ms);
    var bytes=ms.ToArray();
    var valid=type switch {
     "image/jpeg"=>bytes.Length>=3&&bytes[0]==255&&bytes[1]==216&&bytes[2]==255,
     "image/png"=>bytes.Length>=8&&bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
     "image/webp"=>bytes.Length>=12&&System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(bytes,8,4)=="WEBP",
     _=>false
    };
    if(!valid||bytes.Length==0||bytes.Length>5_242_880) return Results.BadRequest(new{error="الصورة غير صالحة أو تجاوزت 5 ميجابايت"});
    var fileName="episode-"+episodeId+"-"+Guid.NewGuid().ToString("N")+ext;
    await File.WriteAllBytesAsync(Path.Combine(posters,fileName),bytes);
    var path="/posters/"+fileName;
    try { series.SetEpisodePoster(episodeId,path); return Results.Ok(new{posterPath=path}); }
    catch(KeyNotFoundException ex) { try{File.Delete(Path.Combine(posters,fileName));}catch{} return Results.NotFound(new{error=ex.Message}); }
   });
   _server.MapPost("/api/admin/series-poster/{seriesId}",async (HttpContext c,string seriesId,SeriesCatalogService series)=>{
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(seriesId)||seriesId.Length>80) return Results.BadRequest(new{error="معرّف المسلسل غير صالح"});
    var old=series.GetSeries(seriesId); if(old is null) return Results.NotFound(new{error="المسلسل غير موجود"});
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
    var fileName="series-"+Guid.NewGuid().ToString("N")+ext;
    var fullPath=Path.Combine(posters,fileName);
    await File.WriteAllBytesAsync(fullPath,bytes);
    try { series.SaveSeries(new SaveSeriesRequest(old.SeriesId,old.Title,old.Description,old.Year,old.Genre,"/posters/"+fileName,old.FolderPath,old.Section,old.IsPublished,old.CategoryId)); }
    catch { File.Delete(fullPath); throw; }
    return Results.Ok(new{posterPath="/posters/"+fileName});
   });
   _server.MapGet("/posters/{**fileName}",(string fileName)=>{
    if(string.IsNullOrWhiteSpace(fileName)||fileName.Length>500) return Results.NotFound();
    var rootPath=Path.GetFullPath(posters).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
    var relative=fileName.Replace('/',Path.DirectorySeparatorChar).Replace('\\',Path.DirectorySeparatorChar);
    var full=Path.GetFullPath(Path.Combine(posters,relative));
    if(!full.StartsWith(rootPath,StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    if(!File.Exists(full)&&!fileName.Contains('/'))
    {
     var previousPosters=Path.Combine(root,"posters");
     var previousFile=Path.Combine(previousPosters,fileName);
     if(File.Exists(previousFile)) full=previousFile;
     else return Results.NotFound();
    }
    var type=Path.GetExtension(full).ToLowerInvariant() switch {".jpg" or ".jpeg"=>"image/jpeg",".png"=>"image/png",".webp"=>"image/webp",_=>null};
    return type is null?Results.NotFound():Results.File(full,type);
   });
   _server.MapPost("/api/admin/library/bulk-remove",(HttpContext c,BulkLibraryRequest request,LibraryService librarySvc)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if(request.Ids is null||request.Ids.Count<1||request.Ids.Count>5000||request.Ids.Any(id=>string.IsNullOrWhiteSpace(id)||id.Length!=24||!id.All(Uri.IsHexDigit))) return Results.BadRequest(new{error="قائمة الملفات المحددة غير صالحة"});
    var ids=request.Ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if(ids.Any(id=>librarySvc.Resolve(id) is null)) return Results.NotFound(new{error="بعض الملفات المحددة لم تعد موجودة في المكتبة"});
    foreach(var id in ids) librarySvc.RemoveFromLibrary(id);
    return Results.Ok(new{removed=ids.Length,message="تمت إزالة الملفات المحددة من المكتبة فقط، دون حذف الملفات الأصلية"});
   });
   _server.MapPost("/api/admin/library/bulk-delete",(HttpContext c,BulkLibraryRequest request)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    return Results.Conflict(new{error="حذف ملفات الفيديو الأصلية معطّل حفاظاً على مكتبتك. استخدم «إزالة المحدد من المكتبة» لإخفائها دون حذفها من القرص."});
   });
   _server.MapPost("/api/admin/library/{id}/remove",(HttpContext c,string id,LibraryService librarySvc)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    if(string.IsNullOrWhiteSpace(id)||id.Length!=24||!Regex.IsMatch(id,"^[a-fA-F0-9]{24}$")) return Results.BadRequest(new{error="معرّف الفيديو غير صالح"});
    try { librarySvc.RemoveFromLibrary(id); return Results.Ok(new{removed=true,message="تمت إزالة الفيديو من المكتبة دون حذف الملف الأصلي"}); }
    catch(FileNotFoundException ex) { return Results.NotFound(new{error=ex.Message}); }
    catch(Exception ex) { return Results.BadRequest(new{error=ex.Message}); }
   });
   _server.MapDelete("/api/admin/library/{id}",(HttpContext c,string id)=>
   {
    if(!IsAdmin(c)) return Results.NotFound();
    return Results.Conflict(new{error="حذف ملفات الفيديو الأصلية معطّل حفاظاً على مكتبتك. استخدم «إزالة من المكتبة فقط» لإخفائها دون حذفها من القرص."});
   });
   _server.MapGet("/api/progress/{viewerId}",(string viewerId,WatchProgressService db)=>viewerId.Length<=80?Results.Ok(db.Get(viewerId)):Results.BadRequest());
   _server.MapGet("/api/watch-history/{viewerId}",(string viewerId,WatchProgressService db)=>viewerId.Length is >0 and <=80?Results.Ok(db.GetViewerHistory(viewerId,500)):Results.BadRequest());
   _server.MapGet("/api/admin/watch-history",(HttpContext c,WatchProgressService db)=>IsAdmin(c)?Results.Ok(db.GetRecentPlayback(200)):Results.NotFound());
   _server.MapGet("/api/admin/watch-counts",(HttpContext c,WatchProgressService db)=>IsAdmin(c)?Results.Ok(new{total=db.GetTotalPlaybackCount(),viewCounts=db.GetViewCounts()}):Results.NotFound());
   _server.MapGet("/api/view-count/{id}",(string id,WatchProgressService db,LibraryService svc,PublicCatalogAccessService publicCatalog)=>svc.Resolve(id) is null||!publicCatalog.IsVideoPublished(id)?Results.NotFound():Results.Ok(new{videoId=id,viewCount=db.GetViewCount(id)}));
   _server.MapPost("/api/watch-events",(WatchPlaybackRequest request,WatchProgressService db,LibraryService svc,PublicCatalogAccessService publicCatalog)=>{if(string.IsNullOrWhiteSpace(request.ViewerId)||request.ViewerId.Length>80||string.IsNullOrWhiteSpace(request.PlaybackId)||request.PlaybackId.Length>80||string.IsNullOrWhiteSpace(request.VideoId)||svc.Resolve(request.VideoId) is null||!publicCatalog.IsVideoPublished(request.VideoId))return Results.BadRequest();db.RecordPlayback(request);return Results.Ok(new{recorded=true});});
   _server.MapPost("/api/progress",(WatchProgressRequest request,WatchProgressService db,LibraryService svc,PublicCatalogAccessService publicCatalog)=>{if(string.IsNullOrWhiteSpace(request.ViewerId)||request.ViewerId.Length>80||string.IsNullOrWhiteSpace(request.VideoId)||svc.Resolve(request.VideoId) is null||!publicCatalog.IsVideoPublished(request.VideoId)||!double.IsFinite(request.PositionSeconds)||!double.IsFinite(request.DurationSeconds)||request.PositionSeconds<0||request.DurationSeconds<=0||request.PositionSeconds>request.DurationSeconds+5||request.PositionSeconds>864000||request.DurationSeconds>864000)return Results.BadRequest();db.Save(request);return Results.Ok(new{saved=true});});
   _server.MapGet("/api/subtitles/{id}",(string id,LibraryService svc,PublicCatalogAccessService publicCatalog)=>svc.Resolve(id) is null||!publicCatalog.IsVideoPublished(id)?Results.NotFound():Results.Ok(svc.ListSubtitles(id)));
   _server.MapGet("/api/subtitles/{id}/{fileName}",(string id,string fileName,LibraryService svc,PublicCatalogAccessService publicCatalog)=>{
    if(svc.Resolve(id) is null||!publicCatalog.IsVideoPublished(id))return Results.NotFound();
    var content=svc.ReadSubtitle(id,fileName);
    return content is null?Results.NotFound():Results.Text(content,"text/vtt; charset=utf-8");
   });
   _server.MapGet("/api/subtitles/{id}/embedded/{streamIndex:int}",async (string id,int streamIndex,MediaConversionService media,LibraryService files,PublicCatalogAccessService publicCatalog)=>
   {
    if(files.Resolve(id) is null||!publicCatalog.IsVideoPublished(id))return Results.NotFound();
    var content=await media.ReadEmbeddedSubtitleAsync(id,streamIndex);
    return content is null?Results.NotFound():Results.Text(content,"text/vtt; charset=utf-8");
   });
   _server.MapGet("/media/{id}",(string id,LibraryService svc,PublicCatalogAccessService publicCatalog)=>{var item=svc.Resolve(id);return item is null||!publicCatalog.IsVideoPublished(id)?Results.NotFound():Results.File(item.FullPath,item.ContentType,enableRangeProcessing:true);});
   await _server.StartAsync(); new MainWindow(root,library,posters,settings.FfmpegPath,settings.Port).Show();
  } catch(Exception ex){
   var details=ex.ToString();
   var logPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AlostoraRestHouse","startup-error.txt");
   try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); File.WriteAllText(logPath,details); } catch { }
   var inner=ex.GetBaseException();
   var message=inner.Message;
   if(inner is System.Net.Sockets.SocketException socket && socket.SocketErrorCode==System.Net.Sockets.SocketError.AddressAlreadyInUse)
    message=$"المنفذ {configuredPort} مستخدم حالياً من برنامج آخر. أغلق البرنامج الذي يستخدمه أو اختر منفذاً آخر من إعدادات الاستراحة.";
   MessageBox.Show("تعذر تشغيل السيرفر:\n"+message+"\n\nتم حفظ تفاصيل الخطأ في:\n"+logPath,"استراحة الأسطورة",MessageBoxButton.OK,MessageBoxImage.Error);
   Shutdown(1);
  }
 }
 private static async Task<bool> IsExistingRestHouseServerAsync(int port)
 {
  try
  {
   using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
   using var response=await client.GetAsync($"http://127.0.0.1:{port}/api/health");
   if(!response.IsSuccessStatusCode)return false;
   await using var stream=await response.Content.ReadAsStreamAsync();
   using var document=await JsonDocument.ParseAsync(stream);
   var json=document.RootElement;
   return json.TryGetProperty("name",out var name)&&name.GetString()=="ALOSTORA REST HOUSE"
    &&json.TryGetProperty("status",out var status)&&status.GetString()=="running";
  }
  catch{return false;}
 }

 private static int? ParseEpisodeNumber(string fileName)
 {
  var name=Path.GetFileNameWithoutExtension(fileName);
  foreach(var pair in new[]{('٠','0'),('١','1'),('٢','2'),('٣','3'),('٤','4'),('٥','5'),('٦','6'),('٧','7'),('٨','8'),('٩','9'),('۰','0'),('۱','1'),('۲','2'),('۳','3'),('۴','4'),('۵','5'),('۶','6'),('۷','7'),('۸','8'),('۹','9')})
   name=name.Replace(pair.Item1,pair.Item2);
  var patterns=new[]{
   @"(?i)S\d{1,3}\s*E(\d{1,4})",
   @"(?i)(?:^|[ ._-])(?:EP|EPISODE)[ ._-]?(\d{1,4})(?:$|[ ._-])",
   @"الحلقة[ ._-]?(\d{1,4})",
   @"(?i)(?:^|[ ._-])E(\d{1,4})(?:$|[ ._-])"
  };
  foreach(var pattern in patterns)
  {
   var match=Regex.Match(name,pattern);
   if(match.Success&&int.TryParse(match.Groups[1].Value,out var number)&&number>0&&number<=10000) return number;
  }
  return null;
 }

 private async void OnExit(object sender,ExitEventArgs e){if(_server is not null){await _server.StopAsync();await _server.DisposeAsync();}}
}