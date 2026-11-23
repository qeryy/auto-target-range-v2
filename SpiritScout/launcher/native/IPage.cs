// 灵枢桌面版（原生）—— 页面契约
//
// 为什么要定这个接口：web 端有 14 个视图。如果每个都硬写进主窗口的 switch 里，
// 加一个页面要改三处（导航、switch、刷新逻辑），删一个页面还得满仓库找残留。
//
// 这里把「一页」抽象成一个类：
//   * 自带 Id / Title / Group（侧栏分组）
//   * 自己负责 CreateView()（一次）和 RefreshAsync()（每次进入）
//   * 注册到 PageRegistry 就自动出现在侧栏
//
// 于是「增加功能」= 新建一个类 + 注册一行；「删除功能」= 删文件 + 删注册行。

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpiritScout.Native
{
    /// <summary>侧栏分组：主功能 / 资源 / 系统。</summary>
    public enum PageGroup
    {
        Main = 0,
        Resource = 1,
        System = 2,
    }

    /// <summary>所有页面实现的契约。</summary>
    public interface IPage
    {
        /// <summary>稳定标识（用于导航高亮与状态恢复）。</summary>
        string Id { get; }

        /// <summary>侧栏显示名。</summary>
        string Title { get; }

        /// <summary>侧栏分组。</summary>
        PageGroup Group { get; }

        /// <summary>首次进入时构建控件树（只调用一次，之后复用）。</summary>
        Control CreateView();

        /// <summary>每次进入（或点刷新）时拉数据。实现里自己 try/catch 并把错误显示在页面上。</summary>
        Task RefreshAsync();
    }

    /// <summary>
    /// 共享上下文：页面用它取 API、弹提示、切页。
    /// 加字段是兼容的（老页面不用管），这比在每个页面里塞一堆构造参数好维护。
    /// </summary>
    public sealed class AppContext
    {
        public SpiritApi Api { get; private set; }
        public Form Shell { get; private set; }

        /// <summary>切换页面（由外壳实现）。</summary>
        public Action<string> Navigate { get; set; }

        /// <summary>在状态栏显示一行提示。</summary>
        public Action<string, bool> Toast { get; set; }

        public AppContext(SpiritApi api, Form shell)
        {
            Api = api;
            Shell = shell;
        }

        /// <summary>统一错误提示：页面里 catch 到异常就调它，别自己 MessageBox。</summary>
        public void ShowError(string what, Exception ex)
        {
            var toast = Toast;
            if (toast != null)
            {
                toast(what + "失败：" + ex.Message, false);
                return;
            }
            MessageBox.Show(Shell, what + "失败：\n\n" + ex.Message, "灵枢",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public void ShowInfo(string message)
        {
            var toast = Toast;
            if (toast != null) toast(message, true);
        }
    }

    /// <summary>页面注册表：增删功能只动这里。</summary>
    public static class PageRegistry
    {
        private static readonly System.Collections.Generic.List<IPage> _pages =
            new System.Collections.Generic.List<IPage>();

        /// <summary>注册一个页面。按注册顺序出现在侧栏。</summary>
        public static void Register(IPage page)
        {
            if (page == null) throw new ArgumentNullException("page");
            foreach (var p in _pages)
            {
                if (p.Id == page.Id)
                    throw new InvalidOperationException("页面 Id 重复：" + page.Id);
            }
            _pages.Add(page);
        }

        /// <summary>注销一个页面（删功能时调）。</summary>
        public static void Unregister(string id)
        {
            for (int i = _pages.Count - 1; i >= 0; i--)
            {
                if (_pages[i].Id == id) _pages.RemoveAt(i);
            }
        }

        public static System.Collections.Generic.IList<IPage> All
        {
            get { return _pages.AsReadOnly(); }
        }

        public static IPage Find(string id)
        {
            foreach (var p in _pages)
            {
                if (p.Id == id) return p;
            }
            return null;
        }

        /// <summary>按分组取页面（侧栏按组渲染）。</summary>
        public static System.Collections.Generic.List<IPage> ByGroup(PageGroup group)
        {
            var list = new System.Collections.Generic.List<IPage>();
            foreach (var p in _pages)
            {
                if (p.Group == group) list.Add(p);
            }
            return list;
        }
    }
}
