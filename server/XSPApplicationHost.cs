//
// Mono.ASPNET.XSPApplicationHost
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2002,2003 Ximian, Inc (http://www.ximian.com)
//
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web;
using System.Xml;
using System.Web.Hosting;
using System.Runtime.Remoting.Lifetime;
#if MODMONO_SERVER
using Mono.Posix;
#endif

namespace Mono.ASPNET
{
	class HttpErrors
	{
		static byte [] error500;

		static HttpErrors ()
		{
			string s = "HTTP/1.0 500 Server error\r\n\r\n" +
				   "<html><head><title>500 Server Error</title><body><h1>Server error</h1>\r\n" +
				   "Your client sent a request that was not understood by this server.\r\n" +
				   "</body></html>\r\n";
			error500 = Encoding.Default.GetBytes (s);
		}

		public static byte [] NotFound (string uri)
		{
			string s = String.Format ("HTTP/1.0 404 Not Found\r\n\r\n" + 
				"<html><head><title>404 Not Found</title></head>\r\n" +
				"<body><h1>Not Found</h1>The requested URL {0} was not found on this " +
				"server.<p>\r\n</body></html>\r\n", uri);

			return Encoding.ASCII.GetBytes (s);
		}

		public static byte [] ServerError ()
		{
			return error500;
		}
	}

	[Serializable]
	class Worker
	{
		[NonSerialized] XSPApplicationServer server;
		IApplicationHost host;
		NetworkStream ns;
		EndOfRequestHandler endOfRequest;
#if MODMONO_SERVER
		ModMonoRequest modRequest;
#else
		RequestData rdata;
		EndPoint remoteEP;
		EndPoint localEP;
#endif

		public Worker (Socket client, EndPoint localEP, XSPApplicationServer server)
		{
			endOfRequest = new EndOfRequestHandler (EndOfRequest);
			ns = new NetworkStream (client, true);
			this.server = server;
#if !MODMONO_SERVER
			try {
				remoteEP = client.RemoteEndPoint;
			} catch { }
			this.localEP = localEP;
#endif
		}

		public void Run (object state)
		{
			try {
#if !MODMONO_SERVER
				if (remoteEP == null)
					return;

				InitialWorkerRequest ir = new InitialWorkerRequest (ns);
				ir.ReadRequestData ();
				rdata = ir.RequestData;
				string vhost = null; // TODO: read the headers in InitialWorkerRequest
				int port = ((IPEndPoint) localEP).Port;
				host = server.GetApplicationForPath (vhost, port, rdata.Path, true);
				if (host == null) {
					byte [] nf = HttpErrors.NotFound (rdata.Path);
					ns.Write (nf, 0, nf.Length);
					ns.Close ();
					return;
				}
#else
				RequestReader rr = new RequestReader (ns);
				string vhost = rr.Request.GetRequestHeader ("Host");
				int port = -1;
				if (vhost != null) {
					int colon = vhost.IndexOf (':');
					if (colon != -1) {
						port = Int32.Parse (vhost.Substring (colon + 1));
						vhost = vhost.Substring (0, colon);
					} else {
						port = 80;
					}
				}
				
				host = server.GetApplicationForPath (vhost, port, rr.GetUriPath (), false);
				if (host == null) {
					rr.Decline ();
					return;
				}
				modRequest = rr.Request;
#endif
				CrossAppDomainDelegate pr = new CrossAppDomainDelegate (ProcessRequest);
				host.Domain.DoCallBack (pr);
			} catch (Exception e) {
				Console.WriteLine (e);
				try {
					byte [] error = HttpErrors.ServerError ();
					ns.Write (error, 0, error.Length);
					ns.Close ();
				} catch {}
			}
		}

		public void ProcessRequest ()
		{
#if !MODMONO_SERVER
			XSPWorkerRequest mwr = new XSPWorkerRequest (ns, host, localEP, remoteEP, rdata);
#else
			XSPWorkerRequest mwr = new XSPWorkerRequest (modRequest, host);
#endif
			if (!mwr.ReadRequestData ()) {
				EndOfRequest (mwr);
				return;
			}
			
			mwr.EndOfRequestEvent += endOfRequest;
			mwr.ProcessRequest ();
		}

		public void EndOfRequest (MonoWorkerRequest mwr)
		{
			try {
				ns.Close ();
			} catch {}
		}
	}

	public class XSPApplicationHost : MarshalByRefObject, IApplicationHost
	{
		string path;
		string vpath;

		public override object InitializeLifetimeService ()
		{
			return null; // who wants to live forever?
		}
		
		public string Path {
			get {
				if (path == null)
					path = AppDomain.CurrentDomain.GetData (".appPath").ToString ();

				return path;
			}
		}

		public string VPath {
			get {
				if (vpath == null)
					vpath =  AppDomain.CurrentDomain.GetData (".appVPath").ToString ();

				return vpath;
			}
		}

		public AppDomain Domain {
			get { return AppDomain.CurrentDomain; }
		}
	}

	public class VPathToHost
	{
		public readonly string vhost;
		public readonly int vport;
		public readonly string vpath;
		public readonly string realPath;

		public IApplicationHost appHost;

		public VPathToHost (string vhost, int vport, string vpath, string realPath)
		{
			this.vhost = vhost;
			this.vport = vport;
			this.vpath = vpath;
			this.realPath = realPath;
			this.appHost = null;
		}

		public void ClearHost ()
		{
			this.appHost = null;
		}

		public void CreateHost ()
		{
			string v = vpath;
			if (v != "/" && v.EndsWith ("/")) {
				v = v.Substring (0, v.Length - 1);
			}
			
			this.appHost = ApplicationHost.CreateApplicationHost (
							typeof (XSPApplicationHost), v, realPath)
							as IApplicationHost;
		}
	}

	public class XSPApplicationServer
	{
		Socket listen_socket;
		bool started;
		bool stop;
		bool verbose;

#if !MODMONO_SERVER
		IPEndPoint bindAddress;
#endif
		Thread runner;

		// a sorted list of mappings. This is much faster than hashtable for typical cases.
		ArrayList vpathToHost = new ArrayList ();
		
 		object marker = new object ();

#if MODMONO_SERVER
		string filename;
#endif

		public XSPApplicationServer ()
		{
#if !MODMONO_SERVER
			SetListenAddress (80);
#endif
		}

#if MODMONO_SERVER
		public void SetListenFile (string filename)
		{
			if (filename == null)
				throw new ArgumentNullException ("filename");

			this.filename = filename;
		}
#else
		public void SetListenAddress (int port)
		{
			SetListenAddress (IPAddress.Any, port);
		}

		public void SetListenAddress (IPAddress address, int port) 
		{
			SetListenAddress (new IPEndPoint (address, port));
		}
		
		public void SetListenAddress (IPEndPoint bindAddress)
		{
			if (started)
				throw new InvalidOperationException ("The server is already started.");

			if (bindAddress == null)
				throw new ArgumentNullException ("bindAddress");

			this.bindAddress = bindAddress;
		}
#endif

		public bool Verbose {
			get { return verbose; }
			set { verbose = value; }
		}

		private void AddApplication (string vhost, int vport, string vpath, string fullPath)
		{
			if (verbose) {
				Console.WriteLine("Registering application:");
				Console.WriteLine("    Host:          {0}", (vhost != null) ? vhost : "any");
				Console.WriteLine("    Port:          {0}", (vport != -1) ?
									     vport.ToString () : "any");

				Console.WriteLine("    Virtual path:  {0}", vpath);
				Console.WriteLine("    Physical path: {0}", fullPath);
			}

			vpathToHost.Add (new VPathToHost (vhost, vport, vpath, fullPath));
		}

 		public void AddApplicationsFromConfigDirectory (string directoryName)
 		{
			if (verbose) {
				Console.WriteLine ("Adding applications from *.webapp files in " +
						   "directory '{0}'", directoryName);
			}

			DirectoryInfo di = new DirectoryInfo (directoryName);
			foreach (FileInfo fi in di.GetFiles ("*.webapp")) {
				AddApplicationsFromConfigFile (fi.FullName);
			}
		}

 		public void AddApplicationsFromConfigFile (string fileName)
 		{
			if (verbose) {
				Console.WriteLine ("Adding applications from config file '{0}'", fileName);
			}

			XmlDocument doc = new XmlDocument ();
			doc.Load (fileName);

			foreach (XmlElement el in doc.SelectNodes ("//web-application")) {
				AddApplicationFromElement (el);
			}
		}

		void AddApplicationFromElement (XmlElement el)
		{
			XmlNode n;

			string name = el.SelectSingleNode ("name").InnerText;
			string vpath = el.SelectSingleNode ("vpath").InnerText;
			string path = el.SelectSingleNode ("path").InnerText;

			string vhost = null;
			n = el.SelectSingleNode ("vhost");
#if !MOD_MONO_SERVER
			if (n != null)
				vhost = n.InnerText;
#else
			// TODO: support vhosts in xsp.exe
			if (verbose)
				Console.WriteLine ("Ignoring vhost {0} for {1}", n.InnerText, name);
#endif

			int vport = -1;
			n = el.SelectSingleNode ("vport");
#if !MOD_MONO_SERVER
			if (n != null)
				vport = Convert.ToInt32 (n.InnerText);
#else
			// TODO: Listen on different ports
			if (verbose)
				Console.WriteLine ("Ignoring vport {0} for {1}", n.InnerText, name);
#endif

			AddApplication (vhost, vport, vpath, path);
		}

 		public void AddApplicationsFromCommandLine (string applications)
 		{
 			if (applications == null)
 				throw new ArgumentNullException ("applications");
 
 			if (applications == "")
				return;

			if (verbose) {
				Console.WriteLine("Adding applications '{0}'...", applications);
			}

 			string [] apps = applications.Split (',');

			foreach (string str in apps) {
				string [] app = str.Split (':');

				if (app.Length < 2 || app.Length > 4)
					throw new ArgumentException ("Should be something like " +
								"[[hostname:]port:]VPath:realpath");

				int vport;
				string vhost;
				string vpath;
				string realpath;
				int pos = 0;

				if (app.Length >= 3) {
					vhost = app[pos++];
				} else {
					vhost = null;
				}

				if (app.Length >= 4) {
					vport = Convert.ToInt16 (app[pos++]);
				} else {
					vport = -1;
				}

				vpath = app [pos++];
				realpath = app[pos++];

				if (!vpath.EndsWith ("/"))
					vpath += "/";
 
 				string fullPath = System.IO.Path.GetFullPath (realpath);
				AddApplication (vhost, vport, vpath, fullPath);
 			}
			// TODO - check for duplicates, sort, optimize, etc.
 		}
 
		public bool Start ()
		{
			if (started)
				throw new InvalidOperationException ("The server is already started.");

 			if (vpathToHost == null)
 				throw new InvalidOperationException ("SetApplications must be called first");

#if MODMONO_SERVER
			if (filename == null)
				throw new InvalidOperationException ("filename not set");

			File.Delete (filename);
			listen_socket = new Socket (AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
			EndPoint ep = new UnixEndPoint (filename);
			listen_socket.Bind (ep);
#else
			listen_socket = new Socket (AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
			listen_socket.Bind (bindAddress);
#endif
			listen_socket.Listen (5);
			runner = new Thread (new ThreadStart (RunServer));
			runner.IsBackground = true;
			runner.Start ();
			stop = false;
			WebTrace.WriteLine ("Server started.");
			return true;
		}

		public void Stop ()
		{
			if (!started)
				throw new InvalidOperationException ("The server is not started.");

			stop = true;	
			listen_socket.Close ();
			lock (vpathToHost) {
				foreach (VPathToHost v in vpathToHost) {
					v.ClearHost ();
				}
			}
			WebTrace.WriteLine ("Server stopped.");
		}

		private void RunServer ()
		{
			started = true;
			Socket client;
			while (!stop){
				client = listen_socket.Accept ();
				WebTrace.WriteLine ("Accepted connection.");
				Worker worker = new Worker (client, client.LocalEndPoint, this);
				ThreadPool.QueueUserWorkItem (new WaitCallback (worker.Run));
			}

			started = false;
		}

		public IApplicationHost GetApplicationForPath (string vhost, int port, string path,
							       bool defaultToRoot)
		{
			VPathToHost bestMatch = null;
			int bestMatchLength = 0;

			// Console.WriteLine("GetApplicationForPath({0},{1},{2},{3})", vhost, port,
			//			path, defaultToRoot);
			foreach (VPathToHost v in vpathToHost) {
				if (v.vport != -1 && v.vport != port) {
					// ports don't match - ignore
					continue;
				}

				if (vhost != null && v.vhost != null &&
				    0 != String.CompareOrdinal(vhost, v.vhost)) {
					// vhosts don't match - ignore
					continue;
				}

				if (path.StartsWith (v.vpath)) {
					int matchLength = v.vpath.Length;
					if (matchLength > bestMatchLength) {
						bestMatchLength = matchLength;
						bestMatch = v;
					}
				}
			}

			if (bestMatch != null) {
				if (bestMatch.appHost == null) {
					lock (vpathToHost) {
						if (bestMatch.appHost == null) {
							bestMatch.CreateHost();
						}
					}
				}
				return bestMatch.appHost;
			} else if (defaultToRoot) {
				return GetApplicationForPath (vhost, port, "/", false);
			} else {
				Console.WriteLine ("No application defined for: {0}:{1}{2}", vhost, port, path);
				return null;
			}
		}
	}
}

