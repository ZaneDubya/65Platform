using System;

namespace HostApp {
    internal class HostLogger {

        public bool IsDebugEnabled { get; protected set; }

        public HostLogger() {
            IsDebugEnabled = true;
        }

        public void Debug(string line, params object[] args) {
            Console.WriteLine(string.Format(line, args));
        }
    }
}
