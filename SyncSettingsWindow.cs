using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Daynote
{
    public sealed class SyncSettingsWindow : Window
    {
        readonly AppController controller;
        readonly TextBlock status;
        readonly TextBlock feedback;
        readonly PasswordBox keyBox;
        readonly Button connect;
        readonly Button manual;
        readonly Button disconnect;
        readonly DispatcherTimer refresh;
        bool validating;
        bool closed;
        public SyncSettingsWindow(AppController controller,bool dark)
        {
            this.controller=controller;
            Title="账号与同步 · 朝夕";Width=520;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=13;
            Background=Ui.Brush(dark?"#1C1C1E":"#FFFFFF");Foreground=Ui.Brush(dark?"#F5F5F7":"#1D1D1F");
            var panel=new StackPanel{Margin=new Thickness(24)};Content=panel;
            panel.Children.Add(new TextBlock{Text="让日程跟随你",FontSize=23,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)});
            panel.Children.Add(new TextBlock{Text="原生多端同步 · 预览版\n每个人使用自己的同步密钥；同一人的设备使用同一密钥。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18)});
            status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18),LineHeight=23};panel.Children.Add(status);
            panel.Children.Add(new TextBlock{Text="个人同步密钥",Margin=new Thickness(0,0,0,6)});
            keyBox=new PasswordBox{Height=36,Padding=new Thickness(8),MaxLength=256,Background=Brushes.White,Foreground=Brushes.Black};
            AutomationProperties.SetName(keyBox,"个人同步密钥");panel.Children.Add(keyBox);
            panel.Children.Add(new TextBlock{Text="密钥相当于账号钥匙，请妥善保管。它只加密保存在这台电脑，不包含在日历导出文件中。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,15),FontSize=11});
            var actions=new StackPanel{Orientation=Orientation.Horizontal};
            connect=Ui.Button("验证并连接",Connect,true,dark);connect.Margin=new Thickness(0,0,8,0);actions.Children.Add(connect);
            manual=Ui.Button("立即同步",delegate { controller.RequestSync();Refresh(); },false,dark);manual.Margin=new Thickness(0,0,8,0);actions.Children.Add(manual);
            disconnect=Ui.Button("断开同步",Disconnect,false,dark);actions.Children.Add(disconnect);panel.Children.Add(actions);
            feedback=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,0)};panel.Children.Add(feedback);
            panel.Children.Add(new TextBlock{Text="断网时可继续编辑，联网后自动重试。另一台设备可能需要打开应用才能取得最新更改。\n断开同步会保留本机日程和原账号绑定。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,15,0,0),FontSize=11});
            refresh=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};refresh.Tick+=delegate{Refresh();};refresh.Start();
            Closed+=delegate{closed=true;refresh.Stop();keyBox.Clear();};Refresh();
        }
        void Refresh()
        {
            if(closed)return;
            status.Text=controller.SyncDescription;
            bool busy=controller.SyncBusy || validating;
            connect.IsEnabled=!busy;keyBox.IsEnabled=!busy;
            manual.IsEnabled=!busy && controller.SyncConnected;
            disconnect.IsEnabled=!busy && controller.SyncConnected;
        }
        void Connect()
        {
            string key=keyBox.Password.Trim();validating=true;feedback.Text="正在验证账号，不会上传日程…";Refresh();
            controller.ValidateSyncKey(key,delegate(SyncResponse response,string error) {
                validating=false;if(closed)return;
                if(error!=null) { feedback.Text=error;Refresh();return; }
                string label=response.AccountLabel ?? "个人日历";
                bool first=controller.Data.Sync==null;
                string prompt=first
                    ?"将连接“"+label+"”。\n\n会先备份本机日历，再将当前 "+controller.Data.Tasks.Count+" 条事项加入此账号的同步，并合并云端已有日程。\n\n请确认这是你自己的账号。是否连接并上传本机日程？"
                    :"将重新连接“"+label+"”，继续发送本机待同步更改并取得其他设备的更新。是否继续？";
                if(MessageBox.Show(this,prompt,"确认账号与首次同步",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) {
                    feedback.Text="已取消连接，本机日程未上传。";Refresh();return;
                }
                error=controller.ConnectSync(key,response);keyBox.Clear();
                feedback.Text=error ?? "已连接。可在上方查看同步进度。";Refresh();
            });
        }
        void Disconnect()
        {
            if(MessageBox.Show(this,"断开后，本机日程和待发送更改仍保留。重新连接时必须使用原账号。","断开同步",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            feedback.Text=controller.DisconnectSync() ?? "已断开同步。";Refresh();
        }
    }
}
