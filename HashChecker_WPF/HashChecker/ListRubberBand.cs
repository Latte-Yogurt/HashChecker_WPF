using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
        /// 任何位置按下都先记下起点，但不立刻接管：单击交给 ListView 的点选与双击，
        /// 小幅移动仍由 ListView 自己按拖动选择处理，只有大幅拖动才变成框选。
        /// 代价是从行上起拖时 ListView 可能先做一点默认拖选，但松手后仍以框选结果为准。
        /// </summary>
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1) return;

            _origin = e.GetPosition(_list);
            _dragging = false;
            _captured = false;
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

            Point current = e.GetPosition(_list);

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

            ApplySelection(rect);

            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
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
            Rect rect = Band(_origin, current);
            ApplySelection(rect);

            _dragging = false;
            HideBand();

            if (_captured)
            {
                _list.ReleaseMouseCapture();
                _captured = false;
            }
        }

        private void ShowOverlay()
        {
            _overlay.Width = _list.ActualWidth;
            _overlay.Height = _list.ActualHeight;
            _overlay.Visibility = Visibility.Visible;
            _band.Visibility = Visibility.Visible;
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
        /// 用「整体替换 SelectedItems」而不是逐项 Add/Remove：后者每项都会触发 SelectionChanged，
        /// 行数一多就明显卡顿。
        /// </summary>
        private void ApplySelection(Rect rect)
        {
            List<CheckItem> hits = RowsIn(rect);

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

            _list.SelectedItems.Clear();
            foreach (CheckItem item in wanted)
            {
                _list.SelectedItems.Add(item);
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
    }
}
