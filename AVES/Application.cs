// Autodesk
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
// AVES
using aRib = AVES.Utilities.Ribbon_Utils;
using AVES.Extensions;

// This application belongs to the root namespace
namespace AVES
{
    // Implementing the interface for applications
    public class Application : IExternalApplication
    {
        #region Properties

        // Make a private uiCtlApp
        private static UIControlledApplication _uiCtlApp;

        #endregion

        #region On startup method

        // This will run on startup
        public Result OnStartup(UIControlledApplication uiCtlApp)
        {
            #region Globals registration

            // Store _uiCtlApp, register on idling
            _uiCtlApp = uiCtlApp;

            try
            {
                _uiCtlApp.Idling += RegisterUiApp;
            }
            catch
            {
                Globals.UiApp = null;
                Globals.UsernameRevit = null;
            }

            // Registering globals
            Globals.RegisterProperties(uiCtlApp);
            Globals.RegisterTooltips($"{Globals.AddinName}.Resources.Files.Tooltips");

            #endregion

            #region Ribbon setup

            // Add ribbon tab
            uiCtlApp.Ext_AddRibbonTab(Globals.AddinName);

            #region Family Edition Panel

            // Create Family Edition panel
            var panelFamilyEdition = uiCtlApp.Ext_AddRibbonPanel(Globals.AddinName, "Family Edition");

            // Add button to panel
            var buttonRenameFamilyNameAndType = panelFamilyEdition.Ext_AddPushButton("Rename\nFamily", "AVES.Commands.Cmd_FAM01_Rename");
            var buttonDownloadFamilies = panelFamilyEdition.Ext_AddPushButton("Download\nFamilies", "AVES.Commands.Cmd_FAM02_Download");

            // Add pulldownbutton to panel
            var pulldownTest = panelFamilyEdition.Ext_AddPulldownButton("Family\nParameters", "AVES.Commands.Cmd_FAM03_Parameters");

            // Add buttons to pulldown
            pulldownTest.Ext_AddPushButton("Add Shared Parameters", "AVES.Commands.Cmd_FAM03a_SharedParams");
            pulldownTest.Ext_AddPushButton("Remove Parameters", "AVES.Commands.Cmd_FAM03b_RemoveParams");
            pulldownTest.Ext_AddPushButton("Fill Family Parameters", "AVES.Commands.Cmd_FAM03c_CsvImporter");

            #endregion

            #region Sheet Creation Panel

            // Create Sheet Creation & Editing panel
            var panelSheets = uiCtlApp.Ext_AddRibbonPanel(Globals.AddinName, "Sheet Creation & Editing");

            // Add "Sheet Creation" pulldownbutton to panel
            var pulldown2 = panelSheets.Ext_AddPulldownButton("Create\nSheets", "AVES.Commands.Cmd_DOC03_CreateSheets");

            // Add buttons to "Sheet Creation" pulldown
            pulldown2.Ext_AddPushButton("Assembly Drawings", "AVES.Commands.Cmd_DOC03a_Assembly");
            pulldown2.Ext_AddPushButton("MT Drawings", "AVES.Commands.Cmd_DOC03b_MT");
            pulldown2.Ext_AddPushButton("Vendor Drawings", "AVES.Commands.Cmd_DOC03c_Vendor");
            pulldown2.Ext_AddPushButton("Korea Drawings", "AVES.Commands.Cmd_DOC03d_Korea");

            // Add "Add Dimensions" pulldownbutton to panel
            var pulldown3 = panelSheets.Ext_AddPulldownButton("Auto-add\nDimensions", "AVES.Commands.Cmd_DOC04_AutoDimension");

            // Add buttons to "Add Dimensions" pulldown
            pulldown3.Ext_AddPushButton("MT Dimensions", "AVES.Commands.Cmd_DOC04a_MT");
            pulldown3.Ext_AddPushButton("Vendor Dimensions", "AVES.Commands.Cmd_DOC04b_Vendor");

            // Add "Add Tags" pulldownbutton to panel
            var pulldown4 = panelSheets.Ext_AddPulldownButton("Auto-tag\nViews", "AVES.Commands.Cmd_DOC05_AutoTag");

            // Add buttons to "Add Tags" pulldown
            pulldown4.Ext_AddPushButton("3D - MT Tags", "AVES.Commands.Cmd_DOC05a_MT");
            pulldown4.Ext_AddPushButton("3D - Vendor Tags", "AVES.Commands.Cmd_DOC05b_Vendor");

            // Add "Auto-fill Schedules" pulldownbutton to panel
            var pulldown5 = panelSheets.Ext_AddPulldownButton("Auto-fill\nSchedules", "AVES.Commands.Cmd_DOC01_FillSchedules");

            // Add buttons to "Auto-fill Schedules" pulldown
            pulldown5.Ext_AddPushButton("By Assembly", "AVES.Commands.Cmd_DOC01a_Assembly");
            pulldown5.Ext_AddPushButton("By Assembly A", "AVES.Commands.Cmd_DOC01b_AssemblyA");

            // Add button to panel
            // var buttonAddNested = panelSheets.Ext_AddPushButton("Add Nested\nto Assemblies", "AVES.Commands.Cmd_DOC08_AddNested");

            // Add Stacked button to panel
            var buttonStack = panelSheets.Ext_AddPushButtonStack(
                "Add Weight", "AVES.Commands.Cmd_DOC07_AddWeight",
                "Fill Sheet Info", "AVES.Commands.Cmd_DOC02_SheetInfo",
                "Tag Reviewer", "AVES.Commands.Cmd_DOC06_TagReviewer");


            // Create data objects of stack
            // var stack1Data = aRib.NewPulldownButtonData("Stack 1", "AVES.Cmds_Stack1");
            // var stack2Data = aRib.NewPulldownButtonData("Stack 2", "AVES.Cmds_Stack2");
            // var stack3Data = aRib.NewPulldownButtonData("Stack 3", "AVES.Cmds_Stack3");

            // Create the stack
            // var stack = panelGeneral.AddStackedItems(stack1Data, stack2Data, stack3Data);
            // var pulldownStack1 = stack[0] as PulldownButton;
            // var pulldownStack2 = stack[1] as PulldownButton;
            // var pulldownStack3 = stack[2] as PulldownButton;

            // Add buttons to stack pulldowns
            // pulldownStack1.Ext_AddPushButton("Button", "AVES.Cmds_Stack1.Cmd_Button");
            // pulldownStack2.Ext_AddPushButton("Button", "AVES.Cmds_Stack2.Cmd_Button");
            // pulldownStack3.Ext_AddPushButton("Button", "AVES.Cmds_Stack3.Cmd_Button");

            #endregion

            #region Modeling Panel

            // Create Modeling panel
            var panelModeling = uiCtlApp.Ext_AddRibbonPanel(Globals.AddinName, "Modeling");

            // Add button to panel
            var buttonMekanoGenerator = panelModeling.Ext_AddPushButton("Mekano\nGenerator", "AVES.Commands.Cmd_MEK01_Generator");
            var buttonMekanoBolts = panelModeling.Ext_AddPushButton("Add\nBolts", "AVES.Commands.Cmd_MEK02_AddBolts");
            var buttonMG3 = panelModeling.Ext_AddPushButton("Cable Tray\nArmour", "AVES.Commands.Cmd_MEK03_TrayArmour");

            #endregion

            #endregion

            // Final return
            return Result.Succeeded;
        }

        #endregion

        #region On shutdown method

        // This will run on shutdown
        public Result OnShutdown(UIControlledApplication uiCtlApp)
        {
            // Cleanup code logic

            // Final return
            return Result.Succeeded;
        }

        #endregion

        #region Use idling to register UiApp

        /// <summary>
        /// Registers the UiApp and Revit username globally.
        /// </summary>
        /// <param name="sender">Sender of the Idling event.</param>
        /// <param name="e">Idling event arguments.</param>
        private static void RegisterUiApp(object sender, IdlingEventArgs e)
        {
            _uiCtlApp.Idling -= RegisterUiApp;

            if (sender is UIApplication uiApp)
            {
                Globals.UiApp = uiApp;
                Globals.UsernameRevit = uiApp.Application.Username;
            }
        }

        #endregion
    }
}
