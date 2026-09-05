using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// The bridge that lets the modeless DM BIM Compliance dashboard talk to Revit.
    ///
    /// A modeless window runs on the WPF thread and has no Revit API context: reading the
    /// document, selecting elements, creating the compliance view or binding parameters from
    /// there throws. Everything the dashboard wants Revit to do is therefore queued here and
    /// executed by Revit itself, on its own thread, through an
    /// <see cref="ExternalEvent"/> — which is exactly what makes it possible to keep working
    /// in Revit while the dashboard stays open.
    ///
    /// Create it from a valid API context (an <see cref="IExternalCommand"/>), keep it alive
    /// as long as the window is open, and dispose it when the window closes.
    /// </summary>
    public sealed class DmRevitTask : IExternalEventHandler, IDisposable
    {
        private readonly object _gate = new object();
        private readonly Queue<Action<UIApplication>> _queue = new Queue<Action<UIApplication>>();
        private ExternalEvent? _event;

        private DmRevitTask()
        {
        }

        /// <summary>Creates the handler and its external event. Must run in a Revit API context.</summary>
        public static DmRevitTask Create()
        {
            var task = new DmRevitTask();
            task._event = ExternalEvent.Create(task);
            return task;
        }

        /// <summary>Queues work for Revit and asks Revit to run it as soon as it is idle.</summary>
        public void Run(Action<UIApplication> action)
        {
            if (action == null)
                return;
            lock (_gate)
            {
                _queue.Enqueue(action);
            }
            try
            {
                _event?.Raise();
            }
            catch
            {
                // Revit refuses the raise while it is shutting down
            }
        }

        /// <summary>Called by Revit on its own thread, in a valid API context.</summary>
        public void Execute(UIApplication app)
        {
            while (true)
            {
                Action<UIApplication> action;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                        return;
                    action = _queue.Dequeue();
                }
                try
                {
                    action(app);
                }
                catch
                {
                    // One failed task must not stop the ones queued behind it; the dashboard
                    // reports failures itself through the status line.
                }
            }
        }

        public string GetName()
        {
            return "APG DM BIM Compliance";
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _queue.Clear();
            }
            try
            {
                _event?.Dispose();
            }
            catch
            {
                // disposing an already-disposed event is not an error worth reporting
            }
            _event = null;
        }
    }
}
