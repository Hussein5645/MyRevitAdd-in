using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Reflection;
using System.Security.Policy;
using System.Windows.Media.Imaging;

namespace MyRevitPlugin
{
    public class Class1 : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            RibbonPanel ribbonPanel = application.CreateRibbonPanel("my ribbon panel");

            string thisAssemblyPath = Assembly.GetExecutingAssembly().Location;
            PushButtonData buttonData = new PushButtonData("testbutton", "my test", thisAssemblyPath, "MyRevitPlugin.MyTest");

            PushButton pushButton = ribbonPanel.AddItem(buttonData) as PushButton;

            pushButton.ToolTip = "hello this is my test";

            Uri urlImage = new Uri(@"C:\icon.png");
            BitmapImage bitmapImage = new BitmapImage(urlImage);
            pushButton.LargeImage = bitmapImage;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
           return Result.Succeeded;
        }

    }

    [Transaction(TransactionMode.Manual)]
    public class MyTest : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var app = uiapp.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            TaskDialog.Show("Revit", "Hello World");

            return Result.Succeeded;
        }
    }
}
