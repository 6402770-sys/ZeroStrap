using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZeroStrap.UI.ViewModels.Settings;

namespace ZeroStrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for ZeroStrapPage.xaml
    /// </summary>
    public partial class ZeroStrapPage
    {
        public ZeroStrapPage()
        {
            DataContext = new ZeroStrapViewModel();
            InitializeComponent();
        }
    }
}
