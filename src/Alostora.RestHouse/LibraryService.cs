using System.Security.Cryptography;
using System.Text;
namespace Alostora.RestHouse;
public sealed record VideoItem(string Id,string Title,string FileName,string Url,string FullPath,string ContentType);
public sealed class LibraryService
{
 private readonly string _root;
 private static readonly Dictionary<string,string> Types=new(StringComparer.OrdinalIgnoreCase){{".mp4","video/mp4"},{".m4v","video/mp4"},{".webm","video/webm"}};
 public LibraryService(string root)=>_root=Path.GetFullPath(root);
 public IReadOnlyList<VideoItem> List()=>Directory.EnumerateFiles(_root,"*.*",SearchOption.AllDirectories).Where(p=>Types.ContainsKey(Path.GetExtension(p))).Select(CreateItem).ToArray();
 public VideoItem? Find(string id)=>List().FirstOrDefault(x=>x.Id==id);
 private VideoItem CreateItem(string path){var rel=Path.GetRelativePath(_root,path);var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rel)))[..24].ToLowerInvariant();return new(id,Path.GetFileNameWithoutExtension(path),Path.GetFileName(path),"/media/"+id,path,Types[Path.GetExtension(path)]);}
}
