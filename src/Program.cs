namespace EpgCollector;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        #if NETFRAMEWORK
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
#else
        ApplicationConfiguration.Initialize();
#endif
        if(args.Contains("--self-test"))return SelfTests.Run();
        if(args.Contains("--integration-test"))return SelfTests.Integration();
        if(args.Contains("--quick-integration-test"))return SelfTests.Integration(true);
        if(args.Contains("--fault-test"))return SelfTests.Fault();
        if(args.Contains("--open-failure-test"))return FailureTests.Run();
        using var single=new Mutex(true,"Local\\CoaxEpgCollector-"+Compat.Hash(System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory)).Substring(0,16),out bool first);
        if(!first){MessageBox.Show("このフォルダーのEDCBepgExは既に起動しています。");return 1;}
        var form=new MainForm();
        if(args.Length==2 && args[0] is "--render-profile" or "--render-schedule")
        {
            string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","EDCB"));
            string chset=Path.Combine(root,"BonDriver_Proxy_PX-MLT(BonDriverProxy).ChSet4.txt");
            using var dialog=new ProfileDialog(new(){Name="PX-MLT",Driver=Path.Combine(root,"BonDriver_Proxy_PX-MLT.dll"),ChSet=chset,Selected=Channels.Read(chset).Select(c=>c.Key).ToList()});
            if(args[0]=="--render-schedule")((TabControl)dialog.Controls.Find("settingsTabs",true).Single()).SelectedIndex=1;
            dialog.Show();Application.DoEvents();using var image=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(image,new Rectangle(Point.Empty,dialog.Size));image.Save(args[1]);dialog.Close();return 0;
        }
        if(args.Length==2 && args[0]=="--render")
        {
            form.Show();Application.DoEvents();
            using var image=new Bitmap(form.Width,form.Height);form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(args[1]);form.Close();return 0;
        }
        Application.Run(form);return 0;
    }
}
