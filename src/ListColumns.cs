namespace EpgCollector;

internal static class ListColumns
{
    public static List<ColumnLayout> Capture(DataGridView grid)=>grid.Columns.Cast<DataGridViewColumn>().Select(c=>new ColumnLayout(c.Name,c.Width,c.DisplayIndex,c.Visible)).ToList();
    public static void Restore(DataGridView grid,IEnumerable<ColumnLayout> layout)
    {
        var saved=layout.ToList();
        foreach(var s in saved.Where(s=>grid.Columns.Contains(s.Name)))
        {
            var c=grid.Columns[s.Name]!;c.Width=Compat.Clamp(s.Width,35,2000);c.Visible=s.Visible;
        }
        int order=0;foreach(var s in saved.OrderBy(s=>s.Order).Where(s=>grid.Columns.Contains(s.Name)))grid.Columns[s.Name]!.DisplayIndex=order++;
        if(!grid.Columns.Cast<DataGridViewColumn>().Any(c=>c.Visible))grid.Columns[0].Visible=true;
    }
    public static void Configure(DataGridView grid,List<ColumnLayout>? saved)
    {
        var defaults=Capture(grid);
        if(saved!=null)Restore(grid,saved);
        var menu=new ContextMenuStrip();
        grid.Disposed+=(_,_)=>menu.Dispose();
        grid.MouseDown+=(_,e)=>
        {
            if(e.Button!=MouseButtons.Right || grid.HitTest(e.X,e.Y).Type!=DataGridViewHitTestType.ColumnHeader)return;
            foreach(ToolStripItem old in menu.Items.Cast<ToolStripItem>().ToList()){menu.Items.Remove(old);old.Dispose();}
            foreach(DataGridViewColumn column in grid.Columns.Cast<DataGridViewColumn>().OrderBy(c=>c.DisplayIndex))
            {
                var item=new ToolStripMenuItem(column.HeaderText){Checked=column.Visible,CheckOnClick=true};
                item.Click+=(_,_)=>{if(!item.Checked&&grid.Columns.Cast<DataGridViewColumn>().Count(c=>c.Visible)==1){item.Checked=true;return;}column.Visible=item.Checked;};menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            var reset=new ToolStripMenuItem("列表示を初期状態に戻す");reset.Click+=(_,_)=>Restore(grid,defaults);menu.Items.Add(reset);
            menu.Show(grid,e.Location);
        };
    }
}
