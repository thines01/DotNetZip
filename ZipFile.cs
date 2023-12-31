// ZipFile.cs
//
// Copyright (c) 2006, 2007 Microsoft Corporation.  All rights reserved.
//
// 
// This class library reads and writes zip files, according to the format
// described by pkware, at:
// http://www.pkware.com/business_and_developers/developer/popups/appnote.txt
//
// This implementation is based on the
// System.IO.Compression.DeflateStream base class in the .NET Framework
// v2.0 base class library.
//
// There are other Zip class libraries available.  For example, it is
// possible to read and write zip files within .NET via the J# runtime.
// But some people don't like to install the extra DLL.  Also, there is
// a 3rd party LGPL-based (or is it GPL?) library called SharpZipLib,
// which works, in both .NET 1.1 and .NET 2.0.  But some people don't
// like the GPL. Finally, there are commercial tools (From ComponentOne,
// XCeed, etc).  But some people don't want to incur the cost.
//
// This alternative implementation is not GPL licensed, is free of cost,
// and does not require J#. It does require .NET 2.0 (for the DeflateStream 
// class).  
// 
// This code is released under the Microsoft Public License . 
// See the License.txt for details.  
//
// Notes:
// This is a simple and naive implementation of zip.
//
// Bugs:
// 1. does not do 0..9 compression levels (not supported by DeflateStream)
// 2. does not do encryption
// 3. no support for reading or writing multi-disk zip archives
// 4. no support for file comments or archive comments
// 5. does not stream as it compresses; all compressed data is kept in memory.
// 6. no support for double-byte chars in filenames
// 7. no support for asynchronous operation
// 
// But it does read and write basic zip files, and it gets reasonable compression. 
//
// NB: PKWare's zip specification states: 
//
// ----------------------
//   PKWARE is committed to the interoperability and advancement of the
//   .ZIP format.  PKWARE offers a free license for certain technological
//   aspects described above under certain restrictions and conditions.
//   However, the use or implementation in a product of certain technological
//   aspects set forth in the current APPNOTE, including those with regard to
//   strong encryption or patching, requires a license from PKWARE.  Please 
//   contact PKWARE with regard to acquiring a license.
// ----------------------
//    
// Fri, 31 Mar 2006  14:43
//
// update Thu, 22 Feb 2007  19:03
//  Fixed a problem with archives that had bit-3 (0x0008) set, 
//  where the CRC, Compressed Size, and Uncompressed size 
//  actually followed the compressed file data. 
//


using System;


namespace Ionic.Utils.Zip
{

    /// <summary>
    /// The ZipFile type represents a zip archive file.  This is the main type in the 
    /// class library that reads and writes zip files, as defined in the format
    /// for zip described by PKWare.  This implementation is based on the
    /// System.IO.Compression.DeflateStream base class in the .NET Framework
    /// v2.0 base class library.
    /// </summary>
    public class ZipFile : System.Collections.Generic.IEnumerable<ZipEntry>,
      IDisposable
    {
        /// <summary>
        /// This read-only property specifies the name of the zipfile to read or write. It is 
        /// set when the instance of the ZipFile type is created.
        /// </summary>
        public string Name
        {
            get { return _name; }
        }


        /// <summary>
        /// This property is read/write for the zipfile. It allows the application to
        /// specify a comment for the zipfile, or read the comment for the zipfile. 
        /// </summary>
        public string Comment
        {
            get { return _Comment; }
            set { _Comment = value;
            _contentsChanged = true;
            }
        }

        /// <summary>
        /// When this is set, any volume name (eg C:) is trimmed 
        /// from fully-qualified pathnames on any ZipEntry, before writing the 
        /// ZipEntry into the ZipFile. 
        /// </summary>
        /// <remarks>
        /// The default value is true. This setting must be true to allow 
        /// Windows Explorer to read the zip archives properly. 
        /// The property is included for backwards compatibility only.  You'll 
        /// almost never need to set this to false.
        /// </remarks>
        ///
        public bool TrimVolumeFromFullyQualifiedPaths
        {
            get { return _TrimVolumeFromFullyQualifiedPaths; }
            set { _TrimVolumeFromFullyQualifiedPaths = value; }
        }

        /// <summary>
        /// Determines whether verbose output is sent to Output 
        /// during <c>AddXxx()</c> and <c>ReadXxx()</c> operations. 
        /// </summary>
        private bool Verbose
        {
            get { return (_Output != null); }
            //set { _Verbose = value; }
        }


        /// <summary>
        /// Gets or sets the TextWriter for the instance. If the TextWriter
        /// is set to a non-null value, then verbose output is sent to the 
        /// TextWriter during Add and Read operations.  
        /// </summary>
        /// <example>
        /// <code>
        /// ZipFile zf= new ZipFile(FilePath);
        /// zf.Output= System.Console.Out;
        /// zf.ExtractAll();
        /// </code>
        /// </example>
        public System.IO.TextWriter Output
        {
            get { return _Output; }
            set { _Output = value; }
        }

        private System.IO.Stream ReadStream
        {
            get
            {
                if (_readstream == null)
                
                if (_name != null)
                    _readstream = System.IO.File.OpenRead(_name);
                
                return _readstream;
            }
            set
            {
                if (value != null)
                    throw new Exception("Cannot set ReadStream explicitly to a non-null value.");
                _readstream = null;
            }
        }

        private System.IO.FileStream WriteStream
        {
            get
            {
                if (_writestream == null)
                {
                    _temporaryFileName = System.IO.Path.GetRandomFileName();
                    _writestream = new System.IO.FileStream(_temporaryFileName, System.IO.FileMode.CreateNew);
                }
                return _writestream;
            }
            set
            {
                if (value != null)
                    throw new Exception("Cannot set the stream to a non-null value.");
                _writestream = null;
            }
        }

        /// <summary>
        /// The default constructor is private.
        /// Users of the library are expected to create an instance of the ZipFile 
        /// class via the parameterized constructors: passing in a filename for the zip 
        /// archive, or via the static Read() method. 
        /// </summary>
        private ZipFile() { }




        /// <summary>
        /// Creates a new ZipFile instance, using the specified ZipFileName for the filename. 
        /// The ZipFileName may be fully qualified.
        /// </summary>
        /// <remarks>
        /// <para>Applications can use this constructor to create a new ZipFile for writing, 
        /// or to slurp in an existing zip archive for read and write purposes.  
        /// </para>
        /// <para>Typically an application writing a zip archive will call this constructor, passing
        /// the name of a file that does not exist, then add 
        /// directories or files to the ZipFile via AddDirectory or AddFile, and then write the 
        /// zip archive to the disk by calling <c>Save()</c>. The file is not actually written to the disk 
        /// until the application calls <c>ZipFile.Save()</c> .
        /// </para>
        /// <para>
        /// An application reading a zip archive can call this constructor, passing the name of a 
        /// zip file that does exist.  The file is then read into the <c>ZipFile</c> instance.  The app
        /// can then enumerate the entries or can add a new entry.  An application may wish to 
        /// explicitly specify that it is reading an existing zip file by using <c>ZipFile.Read()</c>. 
        /// The parameterized constructor allows applications to use the same code to add items 
        /// to a zip archive, regardless of whether the zip file exists.  
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// using (ZipFile zip = new ZipFile(args[0]))
        /// { 
        ///   // note: this does not recurse directories! 
        ///   String[] filenames = System.IO.Directory.GetFiles(args[1]);
        ///   foreach (String filename in filenames)
        ///   {
        ///     Console.WriteLine("Adding {0}...", filename);
        ///     zip.AddFile(filename);
        ///   }  
        ///   zip.Save();
        /// }
        /// </code>
        /// </example>
        /// 
        /// <param name="ZipFileName">The filename to use for the new zip archive.</param>
        public ZipFile(string ZipFileName)
        {
            InitFile(ZipFileName, null);
        }

        /// <summary>
        /// Creates a new ZipFile instance, using the specified ZipFileName for the filename. 
        /// The ZipFileName may be fully qualified.
        /// </summary>
        /// <remarks>
        /// <para>Applications can use this constructor to create a new ZipFile for writing, 
        /// or to slurp in an existing zip archive for read and write purposes.  
        /// </para>
        /// <para>Typically an application writing a zip archive will call this constructor, passing
        /// the name of a file that does not exist, then add 
        /// directories or files to the ZipFile via AddDirectory or AddFile, and then write the 
        /// zip archive to the disk by calling <c>Save()</c>. The file is not actually written to the disk 
        /// until the application calls <c>ZipFile.Save()</c> .
        /// </para>
        /// <para>
        /// An application reading a zip archive can call this constructor, passing the name of a 
        /// zip file that does exist.  The file is then read into the <c>ZipFile</c> instance.  The app
        /// can then enumerate the entries or can add a new entry.  An application may wish to 
        /// explicitly specify that it is reading an existing zip file by using <c>ZipFile.Read()</c>. 
        /// The parameterized constructor allows applications to use the same code to add items 
        /// to a zip archive, regardless of whether the zip file exists.  
        /// </para>
        /// <para>
        /// This version of the constructor allows the caller to pass in a TextWriter, to which verbose 
        /// messages will be written during extraction or creation of the zip archive.  A console application
        /// may wish to pass System.Console.Out to get messages on the Console. A graphical or headless application
        /// may wish to capture the messages in a different TextWriter. 
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// using (ZipFile zip = new ZipFile(args[0]))
        /// { 
        ///   // note: this does not recurse directories! 
        ///   String[] filenames = System.IO.Directory.GetFiles(args[1]);
        ///   foreach (String filename in filenames)
        ///   {
        ///     Console.WriteLine("Adding {0}...", filename);
        ///     zip.AddFile(filename);
        ///   }  
        ///   zip.Save();
        /// }
        /// </code>
        /// </example>
        /// 
        /// <param name="ZipFileName">The filename to use for the new zip archive.</param>
        /// <param name="Output">The output TextWriter to use for verbose messages.</param>
        public ZipFile(string ZipFileName, System.IO.TextWriter Output)
        {
            InitFile(ZipFileName, Output);
        }


        private void InitFile(string ZipFileName, System.IO.TextWriter Output)
        {
            // create a new zipfile
            _name = ZipFileName;
            _Output = Output;
            if (!_name.EndsWith(".zip"))
                throw new System.Exception(String.Format("The file name given ({0}) is a bad format.  It must end with a .zip extension.", ZipFileName));
            if (System.IO.File.Exists(_name))
            {
                // throw new System.Exception(String.Format("That file ({0}) already exists.", ZipFileName));
                ReadIntoInstance(this);
                this._fileAlreadyExists = true;
            }
            else
                _entries = new System.Collections.Generic.List<ZipEntry>();
            return;
        }


        #region For Writing Zip Files

        /// <summary>
        /// Adds an item, either a file or a directory, to a zip file archive.  
        /// </summary>
        /// 
        /// <remarks>
        /// <para>
        /// If adding a directory, the add is recursive on all files and subdirectories 
        /// contained within it. 
        /// </para>
        /// <para>
        /// The name of the item may be a relative path or a fully-qualified path.
        /// The item added by this call to the ZipFile is not written to the zip file
        /// archive until the application calls Save() on the ZipFile. 
        /// </para>
        /// <para>
        /// The directory name used for the file within the archive is the same as
        /// the directory name (potentially a relative path) specified in the FileOrDirectoryName.
        /// </para>
        /// </remarks>
        /// <overloads>This method has two overloads.</overloads>
        /// <param name="FileOrDirectoryName">the name of the file or directory to add.</param>
        /// 
        public void AddItem(string FileOrDirectoryName)
        {
             AddItem(FileOrDirectoryName, null);
        }


        /// <summary>
        /// Adds an item, either a file or a directory, to a zip file archive.  
        /// </summary>
        /// 
        /// <remarks>
        /// <para>
        /// If adding a directory, the add is recursive on all files and subdirectories 
        /// contained within it. 
        /// </para>
        /// <para>
        /// The name of the item may be a relative path or a fully-qualified path.
        /// The item added by this call to the ZipFile is not written to the zip file
        /// archive until the application calls Save() on the ZipFile. 
        /// </para>
        /// 
        /// <para>
        /// This version of the method allows the caller to explicitly specify the 
        /// directory path to be used in the archive. 
        /// </para>
        /// 
        /// </remarks>
        /// 
        /// <param name="FileOrDirectoryName">the name of the file or directory to add.</param>
        /// <param name="DirectoryPathInArchive">
        /// The name of the directory path to use within the zip archive. 
        /// This path may, or may not, correspond to a real directory in the current filesystem.
        /// If the files within the zip are later extracted, this is the path used for the extracted file. 
        /// Passing null will use the path on the FileOrDirectoryName.  Passing the empty string ("")
        /// will insert the item at the root path within the archive. Passing null (nothing in VB) will
        /// use the path (if any) on the filename itself. 
        /// </param>
        /// 
        public void AddItem(String FileOrDirectoryName, String DirectoryPathInArchive)
        {
            if (System.IO.File.Exists(FileOrDirectoryName))
                AddFile(FileOrDirectoryName, DirectoryPathInArchive);
            else if (System.IO.Directory.Exists(FileOrDirectoryName))
                AddDirectory(FileOrDirectoryName, DirectoryPathInArchive);

            else
                throw new Exception(String.Format("That file or directory ({0}) does not exist!", FileOrDirectoryName));
        }

        /// <summary>
        /// Adds a File to a Zip file archive. The name of the file may be a relative path or 
        /// a fully-qualified path. 
        /// </summary>
        /// <remarks>
        /// The file added by this call to the ZipFile is not written to the zip file
        /// archive until the application calls Save() on the ZipFile. 
        /// </remarks>
        /// <overloads>This method has two overloads.</overloads>
        /// <param name="FileName">the name of the file to add.</param>
        /// <returns>The ZipEntry corresponding to the File added.</returns>
        public ZipEntry AddFile(string FileName)
        {
            return AddFile(FileName, null);
        }

        /// <summary>
        /// Adds a File to a Zip file archive. The name of the file may be a relative path or 
        /// a fully-qualified path. 
        /// </summary>
        /// 
        /// <remarks>
        /// <para>
        /// The file added by this call to the ZipFile is not written to the zip file
        /// archive until the application calls Save() on the ZipFile. 
        /// </para>
        /// 
        /// <para>
        /// This version of the method allows the caller to explicitly specify the 
        /// directory path to be used in the archive. 
        /// </para>
        /// 
        /// </remarks>
        /// 
        /// <example>
        /// <code>
        ///    try
        ///    {
        ///      using (ZipFile zip = new ZipFile("test2.zip",System.Console.Out))
        ///      {
        ///        zip.AddFile("c:\\photos\\personal\\7440-N49th.png", "images");
        ///        zip.AddFile("c:\\Desktop\\2005_Annual_Report.pdf", "files\\documents");
        ///        zip.AddFile("test2.cs", "files\\text");
        ///
        ///        zip.Save();
        ///      }
        ///    }
        ///    catch (System.Exception ex1)
        ///    {
        ///      System.Console.Error.WriteLine("exception: " + ex1);
        ///    }
        /// </code>
        /// </example>
        /// 
        /// <param name="FileName">the name of the file to add.</param>
        /// <param name="DirectoryPathInArchive">
        /// Specifies a directory path to use to override any path in the FileName.
        /// This path may, or may not, correspond to a real directory in the current filesystem.
        /// If the files within the zip are later extracted, this is the path used for the extracted file. 
        /// Passing null will use the path on the FileName.  Passing the empty string ("")
        /// will insert the item at the root path within the archive.Passing null (nothing in VB) will
        /// use the path (if any) on the filename itself. 
        /// </param>
        /// <returns>The ZipEntry corresponding to the file added.</returns>
        public ZipEntry AddFile(string FileName, String DirectoryPathInArchive)
        {
            ZipEntry ze = ZipEntry.Create(FileName, DirectoryPathInArchive);
            ze.TrimVolumeFromFullyQualifiedPaths = TrimVolumeFromFullyQualifiedPaths;
            if (Verbose) Output.WriteLine("adding {0}...", FileName);
            InsureUniqueEntry(ze);
            _entries.Add(ze);
            _contentsChanged = true;
            return ze;
        }


        private void InsureUniqueEntry(ZipEntry ze1)
        {
            foreach (ZipEntry ze2 in _entries)
            {
                if (_Debug) Console.WriteLine("Comparing {0} to {1}...", ze1.FileName, ze2.FileName);
                if (ze1.FileName == ze2.FileName)
                    throw new Exception(String.Format("The entry '{0}' already exists in the zip archive.", ze1.FileName));
            }

        }

        /// <summary>
        /// Adds a Directory to a Zip file archive. 
        /// </summary>
        /// 
        /// <remarks>
        /// The name of the directory may be 
        /// a relative path or a fully-qualified path. The add operation is recursive,
        /// so that any files or subdirectories within the name directory are also
        /// added to the archive.
        /// </remarks>
        /// 
        /// <param name="DirectoryName">the name of the directory to add.</param>
        public void AddDirectory(string DirectoryName)
        {
            AddDirectory(DirectoryName, null);
        }


        /// <summary>
        /// Adds a Directory to a Zip file archive. 
        /// </summary>
        /// 
        /// <remarks>
        /// The name of the directory may be 
        /// a relative path or a fully-qualified path. The add operation is recursive,
        /// so that any files or subdirectories within the name directory are also
        /// added to the archive.
        /// </remarks>
        /// 
        /// <param name="DirectoryName">the name of the directory to add.</param>
        /// 
        /// <param name="DirectoryPathInArchive">
        /// Specifies a directory path to use to override any path in the DirectoryName.
        /// This path may, or may not, correspond to a real directory in the current filesystem.
        /// If the zip is later extracted, this is the path used for the extracted file or directory. 
        /// Passing null will use the path on the DirectoryName. Passing the empty string ("")
        /// will insert the item at the root path within the archive. Passing null (nothing in VB) will
        /// use the path (if any) on the filename itself. 
        /// </param>
        /// 
        public void AddDirectory(string DirectoryName, String DirectoryPathInArchive)
        {
            if (Verbose) Output.WriteLine("adding {0}...", DirectoryName);

            int filesAdded = 0;
            String[] filenames = System.IO.Directory.GetFiles(DirectoryName);
            foreach (String filename in filenames)
            {
                AddFile(filename, DirectoryPathInArchive);
                filesAdded++;
            }

            // adding a directory with zero files in it.  We need to add this specially. 
            if (filesAdded == 0)
            {
                String dirName= (!DirectoryName.EndsWith("\\")) ? DirectoryName+"\\" :  DirectoryName;

                ZipEntry ze = ZipEntry.Create(dirName, DirectoryPathInArchive);
                ze.TrimVolumeFromFullyQualifiedPaths = TrimVolumeFromFullyQualifiedPaths;
                ze.MarkAsDirectory();
                //if (Verbose) Output.WriteLine("adding {0}...", dirName);
                InsureUniqueEntry(ze);
                _entries.Add(ze);
                _contentsChanged = true;
            }

            String[] dirnames = System.IO.Directory.GetDirectories(DirectoryName);
            foreach (String dir in dirnames)
            {
                // dir is now fully-qualified, but we need a partially qualified name.
                string tail= System.IO.Path.GetFileName(dir).ToString();         
                AddDirectory(dir, System.IO.Path.Combine(DirectoryPathInArchive,tail));
            }
            _contentsChanged = true;
        }

        /// <summary>
        /// Saves the Zip archive, using the name given when the ZipFile was instantiated. 
        /// </summary>
        /// <remarks>
        /// The zip file is written to storage only when the caller calls <c>Save()</c>.  
        /// The Save operation writes the zip content to a temporary file. 
        /// Then, if the zip file already exists (for example when adding an item to a zip archive)
        /// this method will replace the existing zip file with this temporary file.
        /// If the zip file does not already exist, the temporary file is renamed 
        /// to the desired name.  
        /// </remarks>
        public void Save()
        {
            // check if modified, before saving. 
            if (!_contentsChanged) return;

            if (Verbose) Output.WriteLine("Saving....");

            // an entry for each file
            foreach (ZipEntry e in _entries)
            {
                e.Write(WriteStream);
            }

            WriteCentralDirectoryStructure();

            WriteStream.Close();
            WriteStream = null;

            if (_fileAlreadyExists)
                System.IO.File.Replace(_temporaryFileName, _name, null);
            else
                System.IO.File.Move(_temporaryFileName, _name);

            _fileAlreadyExists = true;
        }

        /// <summary>
        /// Save the file to a new zipfile, with the given name. 
        /// </summary>
        /// <remarks>
        /// This is handy when reading a zip archive from a stream 
        /// and you want to modify the archive (add a file, change a 
        /// comment, etc) and then save it to a file. 
        /// </remarks>
        /// <param name="ZipFileName">The name of the zip archive to save to. Existing files will be overwritten with great prejudice.</param>
        public void Save(string ZipFileName)
        {
            _name = ZipFileName;
            _contentsChanged = true;
            _fileAlreadyExists = (System.IO.File.Exists(_name));
            Save();
        }


        private void WriteCentralDirectoryStructure()
        {
            // the central directory structure
            long Start = WriteStream.Length;
            foreach (ZipEntry e in _entries)
            {
                e.WriteCentralDirectoryEntry(WriteStream);  // this writes a ZipDirEntry corresponding to the ZipEntry
            }
            long Finish = WriteStream.Length;

            // now, the footer
            WriteCentralDirectoryFooter(Start, Finish);
        }


        private void WriteCentralDirectoryFooter(long StartOfCentralDirectory, long EndOfCentralDirectory)
        {
            byte[] bytes = new byte[1024];
            int i = 0;
            // signature
            bytes[i++] = (byte)(ZipConstants.EndOfCentralDirectorySignature & 0x000000FF);
            bytes[i++] = (byte)((ZipConstants.EndOfCentralDirectorySignature & 0x0000FF00) >> 8);
            bytes[i++] = (byte)((ZipConstants.EndOfCentralDirectorySignature & 0x00FF0000) >> 16);
            bytes[i++] = (byte)((ZipConstants.EndOfCentralDirectorySignature & 0xFF000000) >> 24);

            // number of this disk
            bytes[i++] = 0;
            bytes[i++] = 0;

            // number of the disk with the start of the central directory
            bytes[i++] = 0;
            bytes[i++] = 0;

            // total number of entries in the central dir on this disk
            bytes[i++] = (byte)(_entries.Count & 0x00FF);
            bytes[i++] = (byte)((_entries.Count & 0xFF00) >> 8);

            // total number of entries in the central directory
            bytes[i++] = (byte)(_entries.Count & 0x00FF);
            bytes[i++] = (byte)((_entries.Count & 0xFF00) >> 8);

            // size of the central directory
            Int32 SizeOfCentralDirectory = (Int32)(EndOfCentralDirectory - StartOfCentralDirectory);
            bytes[i++] = (byte)(SizeOfCentralDirectory & 0x000000FF);
            bytes[i++] = (byte)((SizeOfCentralDirectory & 0x0000FF00) >> 8);
            bytes[i++] = (byte)((SizeOfCentralDirectory & 0x00FF0000) >> 16);
            bytes[i++] = (byte)((SizeOfCentralDirectory & 0xFF000000) >> 24);

            // offset of the start of the central directory 
            Int32 StartOffset = (Int32)StartOfCentralDirectory;  // cast down from Long
            bytes[i++] = (byte)(StartOffset & 0x000000FF);
            bytes[i++] = (byte)((StartOffset & 0x0000FF00) >> 8);
            bytes[i++] = (byte)((StartOffset & 0x00FF0000) >> 16);
            bytes[i++] = (byte)((StartOffset & 0xFF000000) >> 24);

            // zip archive comment 
            if ((Comment == null) || (Comment.Length == 0))
            {
                // no comment!
                bytes[i++] = (byte)0;
                bytes[i++] = (byte)0;
            }
            else
            {
                Int16 commentLength = (Int16) Comment.Length;
                // the size of our buffer defines the max length of the comment we can write
                if (commentLength + i +2 > bytes.Length) commentLength = (Int16)(bytes.Length - i -2);
                bytes[i++] = (byte)(commentLength & 0x00FF);
                bytes[i++] = (byte)((commentLength & 0xFF00) >> 8);
                char[] c = Comment.ToCharArray();
                int j = 0;
                // now actually write the comment itself into the byte buffer
                for (j = 0; (j < commentLength) && (i + j < bytes.Length); j++)
                {
                    bytes[i + j] = System.BitConverter.GetBytes(c[j])[0];
                }
                i += j;
            }

            WriteStream.Write(bytes, 0, i);
        }

        #endregion

        #region For Reading Zip Files

        /// <summary>
        /// Reads a zip file archive and returns the instance.  
        /// </summary>
        /// 
        /// <exception cref="System.Exception">
        /// Thrown if the zipfile cannot be read. The implementation of this 
        /// method relies on <c>System.IO.File.OpenRead()</c>, which can throw
        /// a variety of exceptions, including specific exceptions if a file
        /// is not found, an unauthorized access exception, exceptions for
        /// poorly formatted filenames, and so on. 
        /// </exception>
        /// 
        /// <param name="ZipFileName">
        /// The name of the zip archive to open.  
        /// This can be a fully-qualified or relative pathname.
        /// </param>
        /// 
        /// <overloads>This method has 2 overloads.</overloads>
        /// 
        /// <returns>The instance read from the zip archive.</returns>
        /// 
        public static ZipFile Read(string ZipFileName)
        {
            return ZipFile.Read(ZipFileName, null);
        }


        /// <summary>
        /// Reads a zip file archive and returns the instance.  
        /// </summary>
        /// 
        /// <remarks>
        /// <para>
        /// This version of the method allows the caller to pass in a TextWriter, to which verbose 
        /// messages will be written during extraction or creation of the zip archive.  A console application
        /// may wish to pass System.Console.Out to get messages on the Console. A graphical or headless application
        /// may wish to capture the messages in a different TextWriter. 
        /// </para>
        /// </remarks>
        /// 
        /// <exception cref="System.Exception">
        /// Thrown if the zipfile cannot be read. The implementation of this 
        /// method relies on <c>System.IO.File.OpenRead()</c>, which can throw
        /// a variety of exceptions, including specific exceptions if a file
        /// is not found, an unauthorized access exception, exceptions for
        /// poorly formatted filenames, and so on. 
        /// </exception>
        /// 
        /// <param name="ZipFileName">
        /// The name of the zip archive to open.  
        /// This can be a fully-qualified or relative pathname.
        /// </param>
        /// 
        /// <param name="Output">The <c>System.IO.TextWriter</c> to be used for output messages.</param>
        /// 
        /// <returns>The instance read from the zip archive.</returns>
        /// 
        public static ZipFile Read(string ZipFileName, System.IO.TextWriter Output)
        {
            ZipFile zf = new ZipFile();
            zf._Output = Output;
            zf._name = ZipFileName;
            ReadIntoInstance(zf);
            return zf;
        }

        /// <summary>
        /// Reads a zip archive from a stream.
        /// </summary>
        /// <remarks>
        /// This is useful when the zipfile is contained in a memory buffer (in which
        /// case you can use a MemoryStream or when the zip archive is embedded into
        /// an already-existing stream. The stream is closed when the reading is done. 
        /// </remarks>
        /// <param name="ZipStream">the stream containing the zip data.</param>
        /// <returns>an instance of ZipFile</returns>
        public static ZipFile Read(System.IO.Stream ZipStream)
        {
            return Read(ZipStream, null);
        }

        /// <summary>
        /// Reads a zip archive from a stream.
        /// </summary>
        /// <remarks>
        /// This is useful when the zipfile is contained in a memory buffer (in which
        /// case you can use a MemoryStream or when the zip archive is embedded into
        /// an already-existing stream. The stream is closed when the reading is done. 
        /// This overload allows the caller to specify a TextWriter to which 
        /// Verbose messages are sent. For example, in a console application, System.Console.Out 
        /// works. If the TextWriter is null, no verbose messages are written. 
        /// </remarks>
        /// <param name="ZipStream">the stream containing the zip data.</param>
        /// <param name="Output">The TextWriter to which Verbose messages are written.</param>
        /// <returns>an instance of ZipFile</returns>
        public static ZipFile Read(System.IO.Stream ZipStream, System.IO.TextWriter Output)
        {
            ZipFile zf = new ZipFile();
            zf._Output = Output;
            zf._readstream = ZipStream;
            ReadIntoInstance(zf);
            return zf;
        }

        /// <summary>
        /// Reads a zip archive from a byte array.
        /// </summary>
        /// <remarks>
        /// This is useful when the zipfile is contained in a byte array.  
        /// </remarks>
        /// <param name="buffer">the byte array containing the zip data.</param>
        /// <returns>an instance of ZipFile. The name is null. </returns>
        public static ZipFile Read(byte[] buffer)
        {
            System.IO.MemoryStream ms = new System.IO.MemoryStream(buffer);
            return Read(ms, null);
        }

        /// <summary>
        /// Reads a zip archive from a byte array.
        /// </summary>
        /// <remarks>
        /// This is useful when the zipfile is contained in a byte array. 
        /// This overload allows the caller to specify a TextWriter to which 
        /// Verbose messages are sent. For example, in a console application, System.Console.Out 
        /// works. If the TextWriter is null, no verbose messages are written. 
        /// </remarks>
        /// <param name="buffer">the byte array containing the zip data.</param>
        /// <param name="Output">The TextWriter to which Verbose messages are written.</param>
        /// <returns>an instance of ZipFile. The name is set to null.</returns>
        public static ZipFile Read(byte[] buffer, System.IO.TextWriter Output)
        {
            ZipFile zf = new ZipFile();
            zf._Output = Output;
            zf._readstream = new System.IO.MemoryStream(buffer);
            ReadIntoInstance(zf);
            return zf;
        }

        private static void ReadIntoInstance(ZipFile zf)
        {
            zf._entries = new System.Collections.Generic.List<ZipEntry>();
            ZipEntry e;
            if (zf.Verbose)
                if (zf.Name==null)
                    zf.Output.WriteLine("Reading zip from stream...");
                else
                zf.Output.WriteLine("Reading zip {0}...", zf.Name);

            while ((e = ZipEntry.Read(zf.ReadStream)) != null)
            {
                if (zf.Verbose)
                    zf.Output.WriteLine("  {0}", e.FileName);

                if (zf._Debug) System.Console.WriteLine("  ZipFile::Read(): ZipEntry: {0}", e.FileName);

                zf._entries.Add(e);
            }

            // read the zipfile's central directory structure here.
            zf._direntries = new System.Collections.Generic.List<ZipDirEntry>();

            ZipDirEntry de;
            while ((de = ZipDirEntry.Read(zf.ReadStream)) != null)
            {
                if (zf._Debug) System.Console.WriteLine("  ZipFile::Read(): ZipDirEntry: {0}", de.FileName);
                zf._direntries.Add(de);
                // Housekeeping: Since ZipFile exposes ZipEntry elements in the enumerator, 
                // we need to copy the comment that we grab from the ZipDirEntry
                // into the ZipEntry, so the application can access the comment. 
                // Also since ZipEntry is used to Write zip files, we need to copy the 
                // file attributes to the ZipEntry as appropriate. 
                foreach (ZipEntry e1 in zf._entries)
                {
                    if (e1.FileName == de.FileName)
                    {
                        e1.Comment = de.Comment;
                        if (de.IsDirectory) e1.MarkAsDirectory();
                        break;
                    }
                }
            }

            ReadCentralDirectoryFooter(zf);

            if ((zf.Verbose) && (zf.Comment!=null) && (zf.Comment!=""))
                zf.Output.WriteLine("Zip file Comment: {0}", zf.Comment);

            // when finished slurping in the zip, close the read stream
            zf.ReadStream.Close();
            //zf.ReadStream = null; // this no worky with streams

        }

        private static void ReadCentralDirectoryFooter(ZipFile zf)
        {
            System.IO.Stream s = zf.ReadStream;
            int signature = Ionic.Utils.Zip.Shared.ReadSignature(s);

            // Throw if this is not a signature for "end of central directory record"
            // This is a sanity check.
            if (signature != ZipConstants.EndOfCentralDirectorySignature)
            {
                s.Seek(-4, System.IO.SeekOrigin.Current);
                throw new Exception(String.Format("  ZipFile::Read(): Bad signature ({0:X8}) at position 0x{1:X8}", signature, s.Position));
            }

            // read a bunch of throwaway metadata for supporting multi-disk archives (throwback!)
            // read the comment here
            byte[] block = new byte[16];
            int n = zf.ReadStream.Read(block, 0, block.Length); // discard

            ReadZipFileComment(zf);
        }

        private static void ReadZipFileComment(ZipFile zf)
        {
            // read the comment here
            byte[] block = new byte[2];
            int n = zf.ReadStream.Read(block, 0, block.Length);

            Int16 commentLength = (short)(block[0] + block[1] * 256);
            if (commentLength > 0)
            {
                block = new byte[commentLength];
                n = zf.ReadStream.Read(block, 0, block.Length);
                zf.Comment = Ionic.Utils.Zip.Shared.StringFromBuffer(block, 0, block.Length);
            }
        }


        /// <summary>
        /// Generic IEnumerator support, for use of a ZipFile in a foreach construct.  
        /// </summary>
        /// <example>
        /// This example reads a zipfile of a given name, then enumerates the 
        /// entries in that zip file, and displays the information about each 
        /// entry on the Console.
        /// <code>
        /// using (ZipFile zip = ZipFile.Read(zipfile))
        /// {
        ///   bool header = true;
        ///   foreach (ZipEntry e in zip)
        ///   {
        ///     if (header)
        ///     {
        ///        System.Console.WriteLine("Zipfile: {0}", zip.Name);
        ///        System.Console.WriteLine("Version Needed: 0x{0:X2}", e.VersionNeeded);
        ///        System.Console.WriteLine("BitField: 0x{0:X2}", e.BitField);
        ///        System.Console.WriteLine("Compression Method: 0x{0:X2}", e.CompressionMethod);
        ///        System.Console.WriteLine("\n{1,-22} {2,-6} {3,4}   {4,-8}  {0}",
        ///                     "Filename", "Modified", "Size", "Ratio", "Packed");
        ///        System.Console.WriteLine(new System.String('-', 72));
        ///        header = false;
        ///     }
        ///
        ///     System.Console.WriteLine("{1,-22} {2,-6} {3,4:F0}%   {4,-8}  {0}",
        ///                 e.FileName,
        ///                 e.LastModified.ToString("yyyy-MM-dd HH:mm:ss"),
        ///                 e.UncompressedSize,
        ///                 e.CompressionRatio,
        ///                 e.CompressedSize);
        ///
        ///     e.Extract(e.FileName);
        ///   }
        /// }
        /// </code>
        /// </example>
        /// 
        /// <returns>a generic enumerator suitable for use  within a foreach loop.</returns>
        public System.Collections.Generic.IEnumerator<ZipEntry> GetEnumerator()
        {
            foreach (ZipEntry e in _entries)
                yield return e;
        }

        /// <summary>
        /// IEnumerator support, for use of a ZipFile in a foreach construct.  
        /// </summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Extracts all of the items in the zip archive, to the specified path in the filesystem.
        /// The path can be relative or fully-qualified. 
        /// </summary>
        public void ExtractAll(string path)
        {
            ExtractAll(path, false);
        }

        /// <summary>
        /// Extracts all of the items in the zip archive, to the specified path in the filesystem,  
        /// optionally overwriting any existing files. The path can be relative or fully-qualified. 
        /// </summary>
        /// <remarks>
        /// This method will send output messages to the output stream
        /// if WantVerbose is set on the ZipFile instance. 
        /// </remarks>
        /// <example>
        /// This example extracts all the entries in a zip archive file, 
        /// to the specified target directory.  It handles exceptions that
        /// may be thrown, such as unauthorized access exceptions or 
        /// file not found exceptions. 
        /// <code>
        ///     try 
        ///     {
        ///       using(ZipFile zip= ZipFile.Read(ZipFile))
        ///       {
        ///         zip.ExtractAll(TargetDirectory, true);
        ///       }
        ///     }
        ///     catch (System.Exception ex1)
        ///     {
        ///      System.Console.Error.WriteLine("exception: " + ex1);
        ///     }
        ///
        /// </code>
        /// </example>
        /// 
        /// <param name="path">the path to which the contents of the zipfile are extracted.</param>
        /// <param name="WantOverwrite">true to overwrite any existing files on extraction</param>
        public void ExtractAll(string path, bool WantOverwrite)
        {
            bool header = Verbose;
            foreach (ZipEntry e in _entries)
            {
                if (header)
                {
                    Output.WriteLine("\n{1,-22} {2,-8} {3,4}   {4,-8}  {0}",
                                 "Name", "Modified", "Size", "Ratio", "Packed");
                    Output.WriteLine(new System.String('-', 72));
                    header = false;
                }
                if (Verbose)
                {
                    Output.WriteLine("{1,-22} {2,-8} {3,4:F0}%   {4,-8} {0}",
                                 e.FileName,
                                 e.LastModified.ToString("yyyy-MM-dd HH:mm:ss"),
                                 e.UncompressedSize,
                                 e.CompressionRatio,
                                 e.CompressedSize);
                    if ((e.Comment != null) && (e.Comment != ""))
                        Output.WriteLine("  Comment: {0}", e.Comment);
                }
                e.Extract(path, WantOverwrite);
            }
        }

        /// <summary>
        /// Extract a single item from the archive.  The file, including any qualifying path, 
        /// is created at the current working directory.  
        /// </summary>
        /// <param name="filename">the file to extract. It must be the exact filename, including the path contained in the archive, if any. </param>
        public void Extract(string filename)
        {
            this[filename].Extract();
        }


        /// <summary>
        /// Extract a single item from the archive.  The file, including any qualifying path, 
        /// is created at the current working directory.  
        /// </summary>
        /// <param name="filename">the file to extract. It must be the exact filename, including the path contained in the archive, if any. </param>
        /// <param name="WantOverwrite">True if the caller wants to overwrite any existing files by the given name.</param>
        public void Extract(string filename, bool WantOverwrite)
        {
            this[filename].Extract(WantOverwrite);
        }


        /// <summary>
        /// Extract a single specified file from the archive, to the given stream.  This is 
        /// useful when extracting to Console.Out for example. 
        /// </summary>
        /// <param name="filename">the file to extract.</param>
        /// <param name="s">the stream to extact to.</param>
        public void Extract(string filename, System.IO.Stream s)
        {
            this[filename].Extract(s);
        }

        /// <summary>
        /// This is a name-based indexer into the Zip archive.  
        /// </summary>
        /// <param name="filename">the name of the file in the zip to retrieve.</param>
        /// <returns>The ZipEntry within the Zip archive, given by the specified filename.</returns>
        public ZipEntry this[String filename]
        {
            get
            {
                foreach (ZipEntry e in _entries)
                {
                    if (e.FileName == filename) return e;
                }
                return null;
            }
        }

        #endregion




        #region Destructors and Disposers

        /// <summary>
        /// This is the class Destructor, which gets called implicitly when the instance is destroyed.  
        /// Because the ZipFile type implements IDisposable, this method calls Dispose(false).  
        /// </summary>
        ~ZipFile()
        {
            // call Dispose with false.  Since we're in the
            // destructor call, the managed resources will be
            // disposed of anyways.
            Dispose(false);
        }

        /// <summary>
        /// Handles closing of the read and write streams associated
        /// to the ZipFile, if necessary.  The Dispose() method is generally 
        /// employed implicitly, via a using() {} statement. 
        /// </summary>
        /// <example>
        /// <code>
        /// using (ZipFile zip = ZipFile.Read(zipfile))
        /// {
        ///   foreach (ZipEntry e in zip)
        ///   {
        ///     if (WantThisEntry(e.FileName)) 
        ///       zip.Extract(e.FileName, Console.OpenStandardOutput());
        ///   }
        /// } // Dispose() is called implicitly here.
        /// </code>
        /// </example>
        public void Dispose()
        {
            // dispose of the managed and unmanaged resources
            Dispose(true);

            // tell the GC that the Finalize process no longer needs
            // to be run for this object.
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// The Dispose() method.  It disposes any managed resources, 
        /// if the flag is set, then marks the instance disposed.
        /// This method is typically not called from application code.
        /// </summary>
        /// <param name="disposeManagedResources">indicates whether the method should dispose streams or not.</param>
        protected virtual void Dispose(bool disposeManagedResources)
        {
            if (!this._disposed)
            {
                if (disposeManagedResources)
                {
                    // dispose managed resources
                    if (_readstream != null)
                    {
                        _readstream.Dispose();
                        _readstream = null;
                    }
                    if (_writestream != null)
                    {
                        _writestream.Dispose();
                        _writestream = null;
                    }
                }
                this._disposed = true;
            }
        }
#endregion


        private System.IO.TextWriter _Output = null;
        private System.IO.Stream _readstream;
        private System.IO.FileStream _writestream;
        private bool _Debug = false;
        //private bool _Verbose = false;
        private bool _disposed = false;
        private System.Collections.Generic.List<ZipEntry> _entries = null;
        private System.Collections.Generic.List<ZipDirEntry> _direntries = null;
        private bool _TrimVolumeFromFullyQualifiedPaths = true;
        private string _name;
        private string _Comment;
        private bool _fileAlreadyExists = false;
        private string _temporaryFileName = null;
        private bool _contentsChanged = false;
    }

}


// Example usage: 
// 1. Extracting all files from a Zip file: 
//
//     try 
//     {
//       using(ZipFile zip= ZipFile.Read(ZipFile))
//       {
//         zip.ExtractAll(TargetDirectory, true);
//       }
//     }
//     catch (System.Exception ex1)
//     {
//       System.Console.Error.WriteLine("exception: " + ex1);
//     }
//
// 2. Extracting files from a zip individually:
//
//     try 
//     {
//       using(ZipFile zip= ZipFile.Read(ZipFile)) 
//       {
//         foreach (ZipEntry e in zip) 
//         {
//           e.Extract(TargetDirectory);
//         }
//       }
//     }
//     catch (System.Exception ex1)
//     {
//       System.Console.Error.WriteLine("exception: " + ex1);
//     }
//
// 3. Creating a zip archive: 
//
//     try 
//     {
//       using(ZipFile zip= new ZipFile(NewZipFile)) 
//       {
//
//         String[] filenames= System.IO.Directory.GetFiles(Directory); 
//         foreach (String filename in filenames) 
//         {
//           zip.Add(filename);
//         }
//
//         zip.Save(); 
//       }
//
//     }
//     catch (System.Exception ex1)
//     {
//       System.Console.Error.WriteLine("exception: " + ex1);
//     }
//
//
// ==================================================================
//
//
//
// Information on the ZIP format:
//
// From
// http://www.pkware.com/business_and_developers/developer/popups/appnote.txt
//
//  Overall .ZIP file format:
//
//     [local file header 1]
//     [file data 1]
//     [data descriptor 1]  ** sometimes
//     . 
//     .
//     .
//     [local file header n]
//     [file data n]
//     [data descriptor n]   ** sometimes
//     [archive decryption header] 
//     [archive extra data record] 
//     [central directory]
//     [zip64 end of central directory record]
//     [zip64 end of central directory locator] 
//     [end of central directory record]
//
// Local File Header format:
//         local file header signature     4 bytes  (0x04034b50)
//         version needed to extract       2 bytes
//         general purpose bit flag        2 bytes
//         compression method              2 bytes
//         last mod file time              2 bytes
//         last mod file date              2 bytes
//         crc-32                          4 bytes
//         compressed size                 4 bytes
//         uncompressed size               4 bytes
//         file name length                2 bytes
//         extra field length              2 bytes
//         file name                       varies
//         extra field                     varies
//
//
// Data descriptor:  (used only when bit 3 of the general purpose bitfield is set)
//         local file header signature     4 bytes  (0x08074b50)
//         crc-32                          4 bytes
//         compressed size                 4 bytes
//         uncompressed size               4 bytes
//
//
//   Central directory structure:
//
//       [file header 1]
//       .
//       .
//       . 
//       [file header n]
//       [digital signature] 
//
//
//       File header:  (This is a ZipDirEntry)
//         central file header signature   4 bytes  (0x02014b50)
//         version made by                 2 bytes
//         version needed to extract       2 bytes
//         general purpose bit flag        2 bytes
//         compression method              2 bytes
//         last mod file time              2 bytes
//         last mod file date              2 bytes
//         crc-32                          4 bytes
//         compressed size                 4 bytes
//         uncompressed size               4 bytes
//         file name length                2 bytes
//         extra field length              2 bytes
//         file comment length             2 bytes
//         disk number start               2 bytes
//         internal file attributes **     2 bytes
//         external file attributes ***    4 bytes
//         relative offset of local header 4 bytes
//         file name (variable size)
//         extra field (variable size)
//         file comment (variable size)
//
// ** The internal file attributes, near as I can tell, 
// uses 0x01 for a file and a 0x00 for a directory. 
//
// ***The external file attributes follows the MS-DOS file attribute byte, described here:
// at http://support.microsoft.com/kb/q125019/
// 0x0010 => directory
// 0x0020 => file 
//
//
// End of central directory record:
//
//         end of central dir signature    4 bytes  (0x06054b50)
//         number of this disk             2 bytes
//         number of the disk with the
//         start of the central directory  2 bytes
//         total number of entries in the
//         central directory on this disk  2 bytes
//         total number of entries in
//         the central directory           2 bytes
//         size of the central directory   4 bytes
//         offset of start of central
//         directory with respect to
//         the starting disk number        4 bytes
//         .ZIP file comment length        2 bytes
//         .ZIP file comment       (variable size)
//
// date and time are packed values, as MSDOS did them
// time: bits 0-4 : second
//            5-10: minute
//            11-15: hour
// date  bits 0-4 : day
//            5-8: month
//            9-15 year (since 1980)
//
// see http://www.vsft.com/hal/dostime.htm

