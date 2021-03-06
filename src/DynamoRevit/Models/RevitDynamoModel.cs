﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

using Dynamo.Core.Threading;

using DSIronPython;
using DSNodeServices;

using Dynamo.Models;
using Dynamo.Utilities;

using Revit.Elements;
using RevitServices.Elements;
using RevitServices.Materials;
using RevitServices.Persistence;
using RevitServices.Threading;
using RevitServices.Transactions;

using Category = Revit.Elements.Category;
using Element = Autodesk.Revit.DB.Element;
using View = Autodesk.Revit.DB.View;

namespace Dynamo.Applications.Models
{
    public class RevitDynamoModel : DynamoModel
    {
        /// <summary>
        ///     Flag for syncing up document switches between Application.DocumentClosing and
        ///     Application.DocumentClosed events.
        /// </summary>
        private bool updateCurrentUIDoc;

        #region Events

        public event EventHandler RevitDocumentChanged;
        public virtual void OnRevitDocumentChanged()
        {
            if (RevitDocumentChanged != null)
                RevitDocumentChanged(this, EventArgs.Empty);
        }

        #endregion

        #region Properties/Fields
        internal override string AppVersion
        {
            get
            {
                return base.AppVersion + 
                    "-R" + DocumentManager.Instance.CurrentUIApplication.Application.VersionBuild;
            }
        }
     
        #endregion

        #region Constructors

        public new static RevitDynamoModel Start()
        {
            return Start(new StartConfiguration());
        }

        public new static RevitDynamoModel Start(StartConfiguration configuration)
        {
            // where necessary, assign defaults
            if (string.IsNullOrEmpty(configuration.Context))
                configuration.Context = Core.Context.REVIT_2015;
            if (string.IsNullOrEmpty(configuration.DynamoCorePath))
            {
                var asmLocation = Assembly.GetExecutingAssembly().Location;
                configuration.DynamoCorePath = Path.GetDirectoryName(asmLocation);
            }

            if (configuration.Preferences == null)
                configuration.Preferences = new PreferenceSettings();

            return new RevitDynamoModel(configuration);
        }

        private RevitDynamoModel(StartConfiguration configuration) :
            base(configuration)
        {
            RevitServicesUpdater.Initialize(DynamoRevitApp.ControlledApplication, DynamoRevitApp.Updaters);
            SubscribeRevitServicesUpdaterEvents();

            InitializeDocumentManager();
            SubscribeDocumentManagerEvents();
            SubscribeTransactionManagerEvents();

            MigrationManager.MigrationTargets.Add(typeof(WorkspaceMigrationsRevit));

            SetupPython();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// This call is made during start-up sequence after RevitDynamoModel 
        /// constructor returned. Virtual methods on DynamoModel that perform 
        /// initialization steps should only be called from here.
        /// </summary>
        internal void HandlePostInitialization()
        {
            InitializeMaterials(); // Initialize materials for preview.
        }

        private bool setupPython;
        private void SetupPython()
        {
            if (setupPython) return;

            IronPythonEvaluator.OutputMarshaler.RegisterMarshaler(
                (Element element) => element.ToDSType(true));

            // Turn off element binding during iron python script execution
            IronPythonEvaluator.EvaluationBegin += (a, b, c, d, e) => ElementBinder.IsEnabled = false;
            IronPythonEvaluator.EvaluationEnd += (a, b, c, d, e) => ElementBinder.IsEnabled = true;

            // register UnwrapElement method in ironpython
            IronPythonEvaluator.EvaluationBegin += (a, b, scope, d, e) =>
            {
                var marshaler = new DataMarshaler();
                marshaler.RegisterMarshaler((Revit.Elements.Element element) => element.InternalElement);
                marshaler.RegisterMarshaler((Category element) => element.InternalCategory);

                Func<object, object> unwrap = marshaler.Marshal;
                scope.SetVariable("UnwrapElement", unwrap);
            };

            setupPython = true;
        }

        private void InitializeDocumentManager()
        {
            // Set the intitial document.
            if (DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument != null)
            {
                DocumentManager.Instance.CurrentUIDocument =
                    DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument;
                Logger.LogWarning(GetDocumentPointerMessage(), WarningLevel.Moderate);
            }
        }

        private static void InitializeMaterials()
        {
            // Ensure that the current document has the needed materials
            // and graphic styles to support visualization in Revit.
            var mgr = MaterialsManager.Instance;
            IdlePromise.ExecuteOnIdleAsync(mgr.InitializeForActiveDocumentOnIdle);
        }

        #endregion

        #region Event subscribe/unsubscribe

        private void SubscribeRevitServicesUpdaterEvents()
        {
            RevitServicesUpdater.Instance.ElementsDeleted += RevitServicesUpdater_ElementsDeleted;
            RevitServicesUpdater.Instance.ElementsModified += RevitServicesUpdater_ElementsModified;
        }

        private void UnsubscribeRevitServicesUpdaterEvents()
        {
            RevitServicesUpdater.Instance.ElementsDeleted -= RevitServicesUpdater_ElementsDeleted;
            RevitServicesUpdater.Instance.ElementsModified -= RevitServicesUpdater_ElementsModified;
        }

        private void SubscribeTransactionManagerEvents()
        {
            TransactionManager.Instance.TransactionWrapper.FailuresRaised += TransactionManager_FailuresRaised;
        }

        private void UnsubscribeTransactionManagerEvents()
        {
            TransactionManager.Instance.TransactionWrapper.FailuresRaised -= TransactionManager_FailuresRaised;
        }

        private void SubscribeDocumentManagerEvents()
        {
            DocumentManager.OnLogError += Logger.Log;
        }

        private void UnsubscribeDocumentManagerEvents()
        {
            DocumentManager.OnLogError -= Logger.Log;
        }

        #endregion

        #region Public methods

        public override void OnEvaluationCompleted(object sender, EvaluationCompletedEventArgs e)
        {
            // finally close the transaction!
            TransactionManager.Instance.ForceCloseTransaction();

            base.OnEvaluationCompleted(sender, e);
        }

#if ENABLE_DYNAMO_SCHEDULER

        protected override void PreShutdownCore(bool shutdownHost)
        {
            if (shutdownHost)
            {
                var uiApplication = DocumentManager.Instance.CurrentUIApplication;
                uiApplication.Idling += ShutdownRevitHostOnce;
            }

            base.PreShutdownCore(shutdownHost);
        }

        private static void ShutdownRevitHostOnce(object sender, IdlingEventArgs idlingEventArgs)
        {
            var uiApplication = DocumentManager.Instance.CurrentUIApplication;
            uiApplication.Idling -= ShutdownRevitHostOnce;
            ShutdownRevitHost();
        }

#else

        protected override void PreShutdownCore(bool shutdownHost)
        {
            if (shutdownHost)
                IdlePromise.ExecuteOnShutdown(ShutdownRevitHost);

            base.PreShutdownCore(shutdownHost);
        }

#endif

        protected override void ShutDownCore(bool shutDownHost)
        {
            DisposeLogic.IsShuttingDown = true;

            base.ShutDownCore(shutDownHost);

            // unsubscribe events
            RevitServicesUpdater.Instance.UnRegisterAllChangeHooks();

            UnsubscribeDocumentManagerEvents();
            UnsubscribeRevitServicesUpdaterEvents();
            UnsubscribeTransactionManagerEvents();
        }

#if !ENABLE_DYNAMO_SCHEDULER

        protected override void PostShutdownCore(bool shutdownHost)
        {
            IdlePromise.ClearPromises();
            IdlePromise.Shutdown();
            base.PostShutdownCore(shutdownHost);
        }

#endif

        /// <summary>
        /// This method is typically called when a new workspace is opened or
        /// when user forcefully resets the engine in the event of an error.
        /// </summary>
        /// <param name="markNodesAsDirty">RequiresRecalc property of all nodes
        /// in the home workspace will be set to 'true' if this parameter is 
        /// true.</param>
        /// 
        public override void ResetEngine(bool markNodesAsDirty = false)
        {
            AsyncTaskCompletedHandler handler =
                _ => OnResetMarkNodesAsDirty(markNodesAsDirty);

            IdlePromise.ExecuteOnIdleAsync(ResetEngineInternal, handler);
        }

        /// <summary>
        /// This event handler is called if 'markNodesAsDirty' in a 
        /// prior call to RevitDynamoModel.ResetEngine was set to 'true'.
        /// </summary>
        /// <param name="markNodesAsDirty"></param>
        private void OnResetMarkNodesAsDirty(bool markNodesAsDirty)
        {
            foreach (var workspace in Workspaces.OfType<HomeWorkspaceModel>())
                workspace.ResetEngine(EngineController, markNodesAsDirty);
        }

        public void SetRunEnabledBasedOnContext(View newView)
        {
            var view = newView as View3D;

            if (view != null && view.IsPerspective
                && Context != Core.Context.VASARI_2014)
            {
                Logger.LogWarning(
                    "Dynamo is not available in a perspective view. Please switch to another view to Run.",
                    WarningLevel.Moderate);
                foreach (var ws in Workspaces.OfType<HomeWorkspaceModel>())
                    ws.RunEnabled = false;
            }
            else
            {
                Logger.Log(
                    string.Format("Active view is now {0}", newView.Name));

                // If there is a current document, then set the run enabled
                // state based on whether the view just activated is 
                // the same document.
                if (DocumentManager.Instance.CurrentUIDocument != null)
                {
                    var newEnabled = newView.Document.Equals(DocumentManager.Instance.CurrentDBDocument);

                    if (!newEnabled)
                    {
                        Logger.LogWarning(
                            "Dynamo is not pointing at this document. Run will be disabled.",
                            WarningLevel.Error);
                    }

                    foreach (var ws in Workspaces.OfType<HomeWorkspaceModel>())
                    {
                        ws.RunEnabled = newEnabled;
                    }
                }
            }
        }

        #endregion

        #region Event handlers 

        /// <summary>
        /// Handler Revit's DocumentOpened event.
        /// It is called when a document is opened, but NOT when a document is 
        /// created from a template.
        /// </summary>
        public void HandleApplicationDocumentOpened()
        {
            // If the current document is null, for instance if there are
            // no documents open, then set the current document, and 
            // present a message telling us where Dynamo is pointing.
            if (DocumentManager.Instance.CurrentUIDocument == null)
            {
                DocumentManager.Instance.CurrentUIDocument = DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument;
                Logger.LogWarning(GetDocumentPointerMessage(), WarningLevel.Moderate);
                foreach (var ws in Workspaces.OfType<HomeWorkspaceModel>())
                    ws.RunEnabled = true;
                ResetForNewDocument();
            }
        }

        /// <summary>
        /// Handler Revit's DocumentClosing event.
        /// It is called when a document is closing.
        /// </summary>
        public void HandleApplicationDocumentClosing(Document doc)
        {
            // ReSharper disable once PossibleUnintendedReferenceComparison
            if (DocumentManager.Instance.CurrentDBDocument.Equals(doc))
            {
                updateCurrentUIDoc = true;
            }
        }

        /// <summary>
        /// Handle Revit's DocumentClosed event.
        /// It is called when a document is closed.
        /// </summary>
        public void HandleApplicationDocumentClosed()
        {
            // If the active UI document is null, it means that all views have been 
            // closed from all document. Clear our reference, present a warning,
            // and disable running.
            if (DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument == null)
            {
                DocumentManager.Instance.CurrentUIDocument = null;
                foreach (var ws in Workspaces.OfType<HomeWorkspaceModel>())
                    ws.RunEnabled = false;
                Logger.LogWarning(
                    "Dynamo no longer has an active document. Please open a document.",
                    WarningLevel.Error);
            }
            else
            {
                // If Dynamo's active UI document's document is the one that was just closed
                // then set Dynamo's active UI document to whatever revit says is active.
                if (updateCurrentUIDoc)
                {
                    updateCurrentUIDoc = false;
                    DocumentManager.Instance.CurrentUIDocument =
                        DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument;
                }
            }

            var uiDoc = DocumentManager.Instance.CurrentUIDocument;
            if (uiDoc != null)
            {
                SetRunEnabledBasedOnContext(uiDoc.ActiveView);
            }
        }

        /// <summary>
        /// Handler Revit's ViewActivated event.
        /// It is called when a view is activated. It is called after the 
        /// ViewActivating event.
        /// </summary>
        public void HandleRevitViewActivated()
        {
            // If there is no active document, then set it to whatever
            // document has just been activated
            if (DocumentManager.Instance.CurrentUIDocument == null)
            {
                DocumentManager.Instance.CurrentUIDocument =
                    DocumentManager.Instance.CurrentUIApplication.ActiveUIDocument;

                InitializeMaterials();
                foreach (var ws in Workspaces.OfType<HomeWorkspaceModel>())
                    ws.RunEnabled = true;
            }
        }

        private static string GetDocumentPointerMessage()
        {
            var docPath = DocumentManager.Instance.CurrentUIDocument.Document.PathName;
            var message = string.IsNullOrEmpty(docPath)
                ? "a new document."
                : string.Format("document: {0}", docPath);
            return string.Format("Dynamo is now running on {0}", message);
        }

        /// <summary>
        ///     Clears all element collections on nodes and resets the visualization manager and the old value.
        /// </summary>
        private void ResetForNewDocument()
        {
            foreach (var ws in Workspaces.OfType<HomeWorkspaceModel>())
            {
                foreach (var node in ws.Nodes)
                    node.MarkNodeAsModified(forceExecute:true);

                ws.OnNodesModified();
                
                foreach (var node in ws.Nodes)
                {
                    lock (node.RenderPackagesMutex)
                    {
                        node.RenderPackages.Clear();
                    }
                }
            }

            OnRevitDocumentChanged();
        }

        private static void ShutdownRevitHost()
        {
            // this method cannot be called without Revit 2014
            var exitCommand = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);
            var uiApplication = DocumentManager.Instance.CurrentUIApplication;

            if ((uiApplication != null) && uiApplication.CanPostCommand(exitCommand))
                uiApplication.PostCommand(exitCommand);
            else
            {
                MessageBox.Show(
                    "A command in progress prevented Dynamo from " +
                        "closing revit. Dynamo update will be cancelled.");
            }
        }

        private void TransactionManager_FailuresRaised(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failList = failuresAccessor.GetFailureMessages();
            
            IEnumerable<FailureMessageAccessor> query =
                from fail in failList
                where fail.GetSeverity() == FailureSeverity.Warning
                select fail;

            foreach (FailureMessageAccessor fail in query)
            {
                Logger.Log("!! Warning: " + fail.GetDescriptionText());
                failuresAccessor.DeleteWarning(fail);
            }
        }

        private void RevitServicesUpdater_ElementsDeleted(Document document, IEnumerable<ElementId> deleted)
        {
            if (!deleted.Any())
                return;

            var nodes = ElementBinder.GetNodesFromElementIds(deleted, CurrentWorkspace, EngineController);
            foreach (var node in nodes)
            {
                node.OnNodeModified(forceExecute:true);
            }
        }

        private void RevitServicesUpdater_ElementsModified(IEnumerable<string> updated)
        {
            var updatedIds = updated.Select(
                x =>
                {
                    Element ret;
                    DocumentManager.Instance.CurrentDBDocument.TryGetElement(x, out ret);
                    return ret;
                }).Select(x => x.Id);
            
            if (!updatedIds.Any())
                return;

            var nodes = ElementBinder.GetNodesFromElementIds(updatedIds, CurrentWorkspace, EngineController);
            foreach (var node in nodes)
            {
                node.OnNodeModified(forceExecute:true);
            }
        }

        #endregion
    }
}
