using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Diagnostics;
namespace Alostora.RestHouse;
public partial class MainWindow:Window
{
 private readonly string _library;
 public MainWindow(string library){InitializeComponent();_library=library;LibraryBox.Text=library;AddressBox.Text=$"http://{GetLanAddress()}:8096";RefreshCount();}
 private void OpenLibrary_Click(object sender,RoutedEventArgs e){try{Directory.CreateDirectory(_library);Process.Start(new ProcessStartInfo("explorer.exe",_library){UseShellExecute=true});}catch(Exception ex){MessageBox.Show("تعذر فتح مجلد المكتبة: "+ex.Message);}}
 private void RefreshLibrary_Click(object sender,RoutedEventArgs e)=>RefreshCount();
 private void RefreshCount(){try{var count=Directory.EnumerateFiles(_library,"*.*",SearchOption.AllDirectories).Count(p=>new[]{".mp4",".m4v",".webm"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase));CountText.Text=$"عدد ملفات الفيديو المكتشفة: {count}";}catch(Exception ex){CountText.Text="تعذر فحص المجلد: "+ex.Message;}}
 private static string GetLanAddress()=>Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x=>x.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(x))?.ToString()??"IP-اللابتوب:8096";
}
