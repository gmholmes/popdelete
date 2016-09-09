using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenPop.Mime;
using OpenPop.Mime.Header;
using OpenPop.Pop3;
using OpenPop.Pop3.Exceptions;
using OpenPop.Common.Logging;
using Message = OpenPop.Mime.Message;

namespace popdelete {

    class Program {
        static bool ReallyDelete = true;
        static Pop3Client pop3Client;
        static bool bStopOnFirst = true;
        static bool bDeleteAll = false;
        static int PopPort = 110;
        static int MaxDays = 7;

        // Read the fetchmailrc file and build a dictionary of host and matching users and passwords and if ssl needed
        static Dictionary<string, Dictionary<string, Dictionary<string, bool>>> ReadRCFile(string path) {
            StreamReader sr = null;

            // Can we access the rc file?
            try {
                sr = new StreamReader(path);
            } catch {
                // Should log something here
                Console.WriteLine("Unable to open rc file: {0}", path);
                return null;
            }

            Console.WriteLine("Using rc file: {0}", path);

            Dictionary<string, Dictionary<string, Dictionary<string, bool>>> dict = new Dictionary<string, Dictionary<string, Dictionary<string, bool>>>();

            // For the whole file...
            while (!sr.EndOfStream) {
                string line = sr.ReadLine();

                if (line.StartsWith("poll ")) {
                    // new host to poll
                    string[] seps = { " " };
                    string[] foo = line.Split(seps, StringSplitOptions.RemoveEmptyEntries);

                    string host = foo[1];

                    // reset the pop port....
                    PopPort = 110;

                    for (int j = 2; j < foo.Length; j++) {
                        if (foo[j].ToLower().Equals("port") || foo[j].ToLower().Equals("service"))
                            PopPort = Int32.Parse(foo[j + 1]);
                    }

                    Dictionary<string, Dictionary<string, bool>> userpass = new Dictionary<string, Dictionary<string, bool>>();

                    while (!sr.EndOfStream) {
                        // Look for user lines...
                        string userline = sr.ReadLine();

                        if (userline.StartsWith("#"))
                            continue;

                        // This is all so wrong in many ways...
                        if (userline.Contains("user ")) {
                            string[] userseps = { " ", ",", ";", "\"", "\t" };
                            string[] userln = userline.Split(userseps, StringSplitOptions.RemoveEmptyEntries);

                            string username = "";
                            string userpasswd = "";
                            bool bSsl = false;

                            for (int i = 0; i < userln.Length; i++) {
                                switch (userln[i]) {
                                    case "user":
                                        username = userln[i + 1];
                                        i++;
                                        break;

                                    case "password":
                                        userpasswd = userln[i + 1];
                                        i++;
                                        break;

                                    // May need to cope with ssl23 version also...
                                    case "ssl":
                                        bSsl = true;
                                        break;

                                    // If we're not keeping messages, skip this one....
                                    case "keep":
                                        if (userln[i - 1] == "no") {
                                            username = "";
                                            userpasswd = "";
                                        }
                                        break;

                                    // As above...
                                    case "flush":
                                        username = "";
                                        userpasswd = "";
                                        break;
                                }
                            }

                            // Add one user and password...
                            if (username != "" && userpasswd != "") {
                                // Strip quotes around names, etc.
                                if (username.StartsWith("'"))
                                    username = username.Remove(0, 1);
                                if (username.EndsWith("'"))
                                    username = username.Remove(username.Length - 1, 1);

                                if (userpasswd.StartsWith("'"))
                                    userpasswd = userpasswd.Remove(0, 1);
                                if (userpasswd.EndsWith("'"))
                                    userpasswd = userpasswd.Remove(userpasswd.Length - 1, 1);

                                Dictionary<string, bool> pws = new Dictionary<string, bool>();
                                pws.Add(userpasswd, bSsl);
                                userpass.Add(username, pws);
                            }
                        } else {
                            dict.Add(host, userpass);
                            userpass = null;
                            // Don't add it again, so break to outer loop
                            break;
                        }
                    }

                    if (userpass != null) {
                        // We may have hit eof...
                        dict.Add(host, userpass);
                        userpass = null;
                    }
                }
            }

            sr.Close();
            sr = null;

            return dict;
        }

        static void Main(string[] args) {
            string RCFile = @"D:\temp\tmpfm.txt";

            Arguments CmdLine = new Arguments(args);

            // -rcfile=path
            if (CmdLine["rc"] != null)
                RCFile = CmdLine["rc"];

            // -maxdays=x
            if (CmdLine["maxdays"] != null) {
                try {
                    MaxDays = Int32.Parse(CmdLine["maxdays"]);
                } catch (FormatException ie) {
                    Console.WriteLine("{0} is not a valid number of days.... {1}", CmdLine["maxdays"], ie.Message);
                    // Adios muchachos...
                    return;
                }
            }

            // --nodelete command line
            if (CmdLine["nodelete"] != null)
                ReallyDelete = false;

            // --nostop command line arg - continue after first younger found...
            if (CmdLine["nostop"] != null)
                bStopOnFirst = false;

            //Just empty the mailbox...
            if (CmdLine["deleteall"] != null)
                bDeleteAll = true;

            CmdLine = null;

            // Load the rc file
            Dictionary<string, Dictionary<string, Dictionary<string, bool>>> hosts = ReadRCFile(RCFile);

            if (hosts != null) {
                // Loop through each host
                foreach (string host in hosts.Keys) {
                    Dictionary<string, Dictionary<string, bool>> users = hosts[host];

                    // Loop through each user
                    foreach (string user in users.Keys) {
                        Dictionary<string, bool> pws = users[user];
                        foreach (string pass in pws.Keys) {
                            PollHost(host, user, pass, pws[pass], MaxDays);
                        }
                    }
                }
            }
        }

        // Poll this host for this user and delete any messages over "days" old.
        static void PollHost(string host, string username, string password, bool ssl, int days) {
            pop3Client = new Pop3Client();
            DateTime cur = DateTime.Now;
            TimeSpan Days = new TimeSpan(days, 0, 0, 0);

            try {
                pop3Client.Connect(host, PopPort, ssl);
                pop3Client.Authenticate(username, password);
            } catch (Exception e) {
                Console.WriteLine("Unable to login user {0} on host {1}, error: {2}",
                    username, host, e.Message);
                return;
            }

            Console.WriteLine("Checking user {0} on host {1}:", username, host);

            int count = pop3Client.GetMessageCount();
            int success = 0;
            int fail = 0;

            // for (int i = count; i >= 1; i -= 1) {
            for (int i = 1; i <= count; i++) {
                try {
                    // We want to check the headers of the message rather than download
                    // the full message
                    MessageHeader Headers = pop3Client.GetMessageHeaders(i);  // 1 based not 0

                    TimeSpan ts = cur - Headers.DateSent;

                    if ((ts > Days) || bDeleteAll) {
                        Console.WriteLine("Deleting message {0:D} sent {1:D}", i, Headers.DateSent);
                        if (ReallyDelete) {
                            pop3Client.DeleteMessage(i);
                        }
                    } else {
                        // We find that POP servers offer messages in date order, so hereafter *all* messages
                        // would fail, so why bother...
                        if (bStopOnFirst) {
                            i = count;
                            break;
                        }
                    }

                    success++;
                } catch (Exception e) {
                    DefaultLogger.Log.LogError(
                        "TestForm: Message fetching failed: " + e.Message + "\r\n" +
                        "Stack trace:\r\n" +
                        e.StackTrace);
                    fail++;
                }
            }

            pop3Client.Disconnect();
            pop3Client.Dispose();
            pop3Client = null;
        }
    }
}
