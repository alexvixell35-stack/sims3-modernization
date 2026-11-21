using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ts3Agent
{
    public delegate object Handler(Args args);

    // Thrown for expected user-facing failures (bad id, nothing selected...).
    public class McpException : Exception
    {
        public McpException(string message) : base(message) { }
    }

    public static class Dispatcher
    {
        static readonly Dictionary<string, Handler> sHandlers = new Dictionary<string, Handler>();

        public static void Register(string name, Handler handler)
        {
            sHandlers[name] = handler;
        }

        public static ICollection<string> Commands { get { return sHandlers.Keys; } }

        // Session lock (kit benchmark mode, docs/315): session_lock {token} claims the channel for one runner; from then on
        // every request must carry the same top-level "token" or it is refused. session_lock {token, release: true} frees it.
        // Never locked = harness behaviour unchanged. Tokens shorter than 16 characters are refused.
        static string sToken;

        static object SessionLock(Dictionary<string, object> a)
        {
            string tok = a != null && a.ContainsKey("token") ? a["token"] as string : null;
            if (tok == null || tok.Length < 16) throw new McpException("session_lock needs a token of at least 16 characters");
            bool release = a.ContainsKey("release") && a["release"] is bool && (bool)a["release"];
            if (sToken != null && sToken != tok) throw new McpException("channel locked by another session");
            sToken = release ? null : tok;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["locked"] = sToken != null;
            return r;
        }

        public static string Handle(string requestJson)
        {
            Dictionary<string, object> req = Json.Parse(requestJson) as Dictionary<string, object>;
            if (req == null) throw new McpException("request must be a JSON object");
            long id = req.ContainsKey("id") ? Convert.ToInt64(req["id"], CultureInfo.InvariantCulture) : 0;
            try
            {
                string cmd = req.ContainsKey("cmd") ? req["cmd"] as string : null;
                Dictionary<string, object> a = req.ContainsKey("args") ? req["args"] as Dictionary<string, object> : null;
                object result;
                if (cmd == "session_lock") result = SessionLock(a);
                else
                {
                    string tok = req.ContainsKey("token") ? req["token"] as string : null;
                    if (sToken != null && tok != sToken) throw new McpException("channel locked by another session");
                    Handler h;
                    if (cmd == null || !sHandlers.TryGetValue(cmd, out h))
                        throw new McpException("unknown command '" + cmd + "'");
                    result = h(new Args(a ?? new Dictionary<string, object>()));
                }
                Dictionary<string, object> ok = new Dictionary<string, object>();
                ok["id"] = id;
                ok["ok"] = true;
                ok["result"] = result;
                return Json.Write(ok);
            }
            catch (Exception e)
            {
                return ErrorReply(id, e);
            }
        }

        public static string ErrorReply(long id, Exception e)
        {
            Dictionary<string, object> err = new Dictionary<string, object>();
            err["id"] = id;
            err["ok"] = false;
            if (e is McpException) err["error"] = e.Message;
            else err["error"] = e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
            return Json.Write(err);
        }
    }

    // Typed accessors over the request's "args" object.
    public class Args
    {
        readonly Dictionary<string, object> mValues;

        public Args(Dictionary<string, object> values) { mValues = values; }
        public ICollection<string> Keys { get { return mValues.Keys; } }  // every argument name, for handlers that pass names through

        public bool Has(string key) { return mValues.ContainsKey(key) && mValues[key] != null; }

        public object Raw(string key) { return Has(key) ? mValues[key] : null; }

        public string Str(string key)
        {
            if (!Has(key)) throw new McpException("missing argument '" + key + "'");
            return Convert.ToString(mValues[key], CultureInfo.InvariantCulture);
        }

        public string Str(string key, string dflt) { return Has(key) ? Str(key) : dflt; }

        public long Long(string key)
        {
            if (!Has(key)) throw new McpException("missing argument '" + key + "'");
            object v = mValues[key];
            if (v is string) return long.Parse((string)v, CultureInfo.InvariantCulture);
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        public long Long(string key, long dflt) { return Has(key) ? Long(key) : dflt; }

        public ulong ULong(string key)
        {
            if (!Has(key)) throw new McpException("missing argument '" + key + "'");
            object v = mValues[key];
            if (v is string) return ulong.Parse((string)v, CultureInfo.InvariantCulture);
            return Convert.ToUInt64(v, CultureInfo.InvariantCulture);
        }

        public double Double(string key)
        {
            if (!Has(key)) throw new McpException("missing argument '" + key + "'");
            object v = mValues[key];
            if (v is string) return double.Parse((string)v, CultureInfo.InvariantCulture);
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public double Double(string key, double dflt) { return Has(key) ? Double(key) : dflt; }

        public bool Bool(string key, bool dflt)
        {
            if (!Has(key)) return dflt;
            object v = mValues[key];
            if (v is bool) return (bool)v;
            return Convert.ToString(v, CultureInfo.InvariantCulture).ToLowerInvariant() == "true";
        }

        public List<object> List(string key)
        {
            return Has(key) ? mValues[key] as List<object> : null;
        }
    }
}
