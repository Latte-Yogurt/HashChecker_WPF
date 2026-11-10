using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HashChecker.Models;

namespace HashChecker
{
    /// <summary>
    /// 列表的框选支持。WPF 的 ListView 原生只有点选、Ctrl 点选与 Shift 范围选，
    /// 不支持按住左键拖出矩形。这里补上：任何位置按下先记起点，移动超过阈值才接管；
    /// 单击与双击仍完全交给 ListView。之所以单独成类而不塞进 MainWindow：
    /// 它涉及鼠标捕获、坐标换算、滚动偏移与虚拟化，逻辑自洽且与窗口其他部分无关。
    ///
    /// 覆盖层必须 IsHitTestVisible=false，绝不能吃掉列表的鼠标事件。
    /// </summary>
    public sealed class ListRubberBand
    {
        /// <summary>
        /// 按下到移动超过这个距离才当成框选：否则「在空白处点一下」会被算成一次零面积框选，
        /// 把用户已有的选择意外清空。阈值也不能太大，否则列表行宽铺满整列、行间只有 1px 时
        /// 用户几乎找不到能起拖的地方。
        /// </summary>
        private const double DragThreshold = 4.0;

        private readonly ListView _list;
        private readonly Canvas _overlay;
        private readonly Rectangle _band;

        private Point _origin;
        private bool _armed;
        private bool _dragging;
        private bool _append;
        private bool _captured;

        /// <summary>框选开始前已选中的行，Ctrl 追加时以它为基准。</summary>
        private readonly List<CheckItem> _baseline = new List<CheckItem>();

        public ListRubberBand(ListView list)
        {
            if (list == null) throw new ArgumentNullException("list");

            _list = list;

            _overlay = new Canvas
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Background = Brushes.Transparent
            };

            _band = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0xA5)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x3A, 0x6E, 0xA5)),
                Visibility = Visibility.Collapsed
            };

            _overlay.Children.Add(_band);

            Install();

            _list.PreviewMouseDown += OnMouseDown;
            _list.PreviewMouseMove += OnMouseMove;
            _list.PreviewMouseUp += OnMouseUp;
            _list.LostMouseCapture += OnLostCapture;
        }

        /// <summary>
        /// 把覆盖层放进列表所在的 Grid 并占同一格，矩形坐标才能直接复用列表的本地坐标。
        /// 窗口在 Loaded 之后调用一次。
        /// </summary>
        public void AttachOverlay(Grid host, int row)
        {
            if (host == null) return;
            if (_overlay.Parent is Panel) return;

            Grid.SetRow(_overlay, row);
            Panel.SetZIndex(_overlay, 100);
            host.Children.Add(_overlay);
        }

        /// <summary>列表尺寸变化时把残留的矩形收掉，否则缩放窗口后会留下一条旧框。</summary>
        private void Install()
        {
            _list.SizeChanged += (s, e) => HideBand();
        }

        /// <summary>
        /// 只在**文件显示区**（内容区）按下才记起点：单击交给 ListView 的点选与双击，
        /// 小幅移动仍由 ListView 自己按拖动选择处理，只有大幅拖动才变成框选。
        /// 代价是从行上起拖时 ListView 可能先做一点默认拖选，但松手后仍以框选结果为准。
        ///
        /// 落在下拉条上的按下必须整条链路放行：这里不记起点、并把 _armed 置 false，
        /// 后面的 OnMouseMove 才不会被**上一次框选留下的陈旧 _origin** 骗过去 ——
        /// 否则拖动超过阈值时那次 CaptureMouse 会把鼠标捕获从滑块上抢走，
        /// 下拉条就再也拖不动了（这正是「只能框选、拖不动下拉条」的成因）。
        /// </summary>
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1) return;

            _dragging = false;
            _captured = false;
            _armed = false;

            Point origin = e.GetPosition(_list);
            if (!IsBandStartAllowed(origin)) return;

            _armed = true;
            _origin = origin;
            _append = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            _baseline.Clear();
            if (_append)
            {
                foreach (object selected in _list.SelectedItems)
                {
                    CheckItem item = selected as CheckItem;
                    if (item != null) _baseline.Add(item);
                }
            }
        }

        /// <summary>拖动过程中实时更新选择，用户能立刻看到哪些行会被选中。</summary>
        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (_dragging) Finish(e.GetPosition(_list));
                return;
            }

            // 按下点不在内容区（例如在下拉条上）：完全不接管，让滑块继续持有捕获。
            if (!_armed) return;

            Point current = ClampToContent(e.GetPosition(_list));

            if (!_dragging)
            {
                if (Math.Abs(current.X - _origin.X) < DragThreshold &&
                    Math.Abs(current.Y - _origin.Y) < DragThreshold)
                {
                    return;
                }

                _dragging = true;

                if (!_captured)
                {
                    _list.CaptureMouse();
                    _captured = true;
                }

                ShowOverlay();
                e.Handled = true;
            }

            Rect rect = Band(_origin, current);
            PositionBand(rect);

            // 拖动途中窗口可能被缩放、滚动条可能因筛选出现或消失，尺寸必须跟着重算。
            SyncOverlaySize();

            ApplySelection(rect);

            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _armed = false;

            if (!_dragging) return;

            Finish(e.GetPosition(_list));
            e.Handled = true;
        }

        /// <summary>
        /// 被别的窗口抢走鼠标时也要把框收掉，避免留一个残影；
        /// 这里不刷新选择：上下文已经丢了，按最后一次拖到的位置结算即可。
        /// </summary>
        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            _dragging = false;
            HideBand();

            if (_captured)
            {
                _captured = false;
            }
        }

        private void Finish(Point current)
        {
            Rect rect = Band(_origin, ClampToContent(current));
            ApplySelection(rect);

            _dragging = false;
            HideBand();

            if (_captured)
            {
                _list.ReleaseMouseCapture();
                _captured = false;
            }
        }

        /// <summary>
        /// 框选可用的内容区 = **文件显示区**：列表里真正放行的那一块，不含任何滚动条。
        ///
        /// 口径：从列表整体尺寸里扣掉**实际可见且有尺寸**的滚动条。
        /// 不能改用 ScrollViewer.ViewportWidth/Height —— 实测它在布局尚未稳定时会给出
        /// 明显偏小的中间值（本工程实测 ViewportHeight 一度只有 5px，按它裁剪会让整个列表都选不中），
        /// 也不能只用 SystemParameters 常量 —— WPF 主题会改滚动条宽度（实测 17.33 而非常量 17）。
        /// </summary>
        private Rect ContentArea()
        {
            double width = _list.ActualWidth;
            double height = _list.ActualHeight;

            ScrollViewer viewer = FindScrollViewer(_list);
            if (viewer != null)
            {
                double barWidth = TotalBarThickness(viewer, Orientation.Vertical);
                double barHeight = TotalBarThickness(viewer, Orientation.Horizontal);

                if (barWidth <= 0 || barHeight <= 0)
                {
                    // 拿不到实测尺寸时退回系统常量，保证「有滚动条就一定扣掉」。
                    if (barWidth <= 0 &&
                        (Visibility)viewer.GetValue(ScrollViewer.ComputedVerticalScrollBarVisibilityProperty) == Visibility.Visible)
                    {
                        barWidth = SystemParameters.VerticalScrollBarWidth;
                    }

                    if (barHeight <= 0 &&
                        (Visibility)viewer.GetValue(ScrollViewer.ComputedHorizontalScrollBarVisibilityProperty) == Visibility.Visible)
                    {
                        barHeight = SystemParameters.HorizontalScrollBarHeight;
                    }
                }

                width -= barWidth;
                height -= barHeight;
            }
            else
            {
                if ((Visibility)_list.GetValue(ScrollViewer.ComputedVerticalScrollBarVisibilityProperty) == Visibility.Visible)
                {
                    width -= SystemParameters.VerticalScrollBarWidth;
                }

                if ((Visibility)_list.GetValue(ScrollViewer.ComputedHorizontalScrollBarVisibilityProperty) == Visibility.Visible)
                {
                    height -= SystemParameters.HorizontalScrollBarHeight;
                }
            }

            if (width < 0) width = 0;
            if (height < 0) height = 0;

            return new Rect(0, 0, width, height);
        }

        /// <summary>
        /// 某个方向上所有可见滚动条占用的厚度之和。正常只有一条；
        /// 累加是为了应对主题把滚动条拆成多个部件的情形，且不会漏掉其中任何一个。
        /// </summary>
        private static double TotalBarThickness(DependencyObject root, Orientation orientation)
        {
            double total = 0;

            ScrollBar bar = root as ScrollBar;
            if (bar != null && bar.Orientation == orientation && bar.IsVisible)
            {
                double size = orientation == Orientation.Vertical ? bar.ActualWidth : bar.ActualHeight;
                if (size > 0 && !double.IsNaN(size)) total += size;
                return total;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                total += TotalBarThickness(VisualTreeHelper.GetChild(root, i), orientation);
            }

            return total;
        }

        /// <summary>按可视树找列表内部的 ScrollViewer。</summary>
        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;

            ScrollViewer direct = root as ScrollViewer;
            if (direct != null) return direct;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                ScrollViewer found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>按下点必须落在内容区里才允许起框选；落在下拉条上时让下拉条自己处理。</summary>
        private bool IsBandStartAllowed(Point point)
        {
            return ContentArea().Contains(point);
        }

        /// <summary>把拖动点收进内容区，框选矩形就不会越到下拉条上面去。</summary>
        private Point ClampToContent(Point point)
        {
            Rect area = ContentArea();

            if (point.X < area.Left) point.X = area.Left;
            if (point.X > area.Right) point.X = area.Right;
            if (point.Y < area.Top) point.Y = area.Top;
            if (point.Y > area.Bottom) point.Y = area.Bottom;

            return point;
        }

        private void ShowOverlay()
        {
            SyncOverlaySize();
            _overlay.Visibility = Visibility.Visible;
            _band.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 覆盖层尺寸跟随当前内容区。窗口大小不固定，拖动过程中可能被缩放，
        /// 也可能因为筛选/滚动让滚动条出现或消失，所以每次移动都要重算一次，
        /// 不能只在拖动开始时设一次尺寸。
        /// </summary>
        private void SyncOverlaySize()
        {
            Rect area = ContentArea();
            _overlay.Width = area.Width;
            _overlay.Height = area.Height;
        }

        private void HideBand()
        {
            _band.Visibility = Visibility.Collapsed;
            _overlay.Visibility = Visibility.Collapsed;
        }

        private void PositionBand(Rect rect)
        {
            Canvas.SetLeft(_band, rect.X);
            Canvas.SetTop(_band, rect.Y);
            _band.Width = rect.Width;
            _band.Height = rect.Height;
        }

        /// <summary>由两个对角点构造规范化矩形（左上角 + 正宽高）。</summary>
        private static Rect Band(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            double w = Math.Abs(a.X - b.X);
            double h = Math.Abs(a.Y - b.Y);
            return new Rect(x, y, w, h);
        }

        /// <summary>
        /// 选中所有与矩形相交的行；_append 为 true 时保留原有选择。
        /// 只对**差集**做增删：清空重建会把已经选中的行先退掉再选回来，
        /// 每移动一次就要触发一整轮 SelectionChanged，行数一多就明显卡顿。
        ///
        /// 矩形先与内容区求交：万一有调用路径绕过 ClampToContent（或滚动条在拖动途中
        /// 才出现），落在下拉条上的那部分也不能参与选行 —— 否则「框在下拉条上」照样能选中行。
        /// </summary>
        private void ApplySelection(Rect rect)
        {
            Rect area = ContentArea();
            rect.Intersect(area);

            List<CheckItem> hits = rect.IsEmpty ? new List<CheckItem>() : RowsIn(rect);

            var wanted = new List<CheckItem>(hits.Count + _baseline.Count);

            if (_append)
            {
                foreach (CheckItem item in _baseline)
                {
                    if (!wanted.Contains(item)) wanted.Add(item);
                }
            }

            foreach (CheckItem item in hits)
            {
                if (!wanted.Contains(item)) wanted.Add(item);
            }

            var current = new List<object>(_list.SelectedItems.Count);
            foreach (object selected in _list.SelectedItems) current.Add(selected);

            foreach (object selected in current)
            {
                CheckItem item = selected as CheckItem;
                if (item == null || !wanted.Contains(item)) _list.SelectedItems.Remove(selected);
            }

            foreach (CheckItem item in wanted)
            {
                if (!_list.SelectedItems.Contains(item)) _list.SelectedItems.Add(item);
            }
        }

        /// <summary>找出与矩形相交的行，行的位置先换算到列表坐标系。</summary>
        private List<CheckItem> RowsIn(Rect rect)
        {
            var result = new List<CheckItem>();

            foreach (object candidate in _list.Items)
            {
                CheckItem item = candidate as CheckItem;
                if (item == null) continue;

                ListViewItem container = _list.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem;
                if (container == null || !container.IsVisible) continue;

                Point topLeft = container.TranslatePoint(new Point(0, 0), _list);
                Rect bounds = new Rect(topLeft.X, topLeft.Y, container.ActualWidth, container.ActualHeight);

                if (rect.IntersectsWith(bounds)) result.Add(item);
            }

            return result;
        }
    }
}
