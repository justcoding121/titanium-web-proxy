using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Titanium.Web.Proxy.Models {
	public class ProxySetting {
		public string Host { get; set; }
		public int Port { get; set; }

		public ProxySetting( string host, int port ) {
			Host = host;
			Port = port;
		}
	}
}
