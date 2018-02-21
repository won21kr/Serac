﻿using System;
using System.Threading.Tasks;
using Serac;

namespace Serac.Katatonic {
	public static class KatatonicModule {
		public static Func<Request, Task<Response>> Serve(Action<App> builder) =>
			new App(builder).Handle;
		
		public static WebServer Katatonic(this WebServer server, string path, Action<App> builder) =>
			server.RegisterHandler(path, Serve(builder));
	}
}