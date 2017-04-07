using System;

namespace Titanium.Web.Proxy
{
	public static class RuntimeHelper
	{
		private static bool? cached;

		public static bool IsMono() {
			if (!cached.HasValue)
				cached = Type.GetType ("Mono.Runtime") != null;

			return cached.Value;
		}
	}
}

