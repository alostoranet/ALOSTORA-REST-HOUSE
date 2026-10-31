using System.Net;
using System.Net.Sockets;
using System.Windows;
namespace Alostora.RestHouse;
public partial class MainWindow:Window
{
 public MainWindow(string library){InitializeComponent();LibraryBox.Text=library;AddressBox.Text=$"http://{GetLanAddress()}:8096";}
 private static string GetLanAddress()=>Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x=>x.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(x))?.ToString()??"IP-اللابتوب:8096";
}
