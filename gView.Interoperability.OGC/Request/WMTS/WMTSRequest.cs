﻿using gView.Drawing.Pro;
using gView.Framework.Carto;
using gView.Framework.Geometry;
using gView.Framework.Geometry.Tiling;
using gView.Framework.Metadata;
using gView.Framework.system;
using gView.MapServer;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace gView.Interoperability.OGC.Request.WMTS
{
    [gView.Framework.system.RegisterPlugIn("A16422D6-F9EB-46C7-9517-2C50713A1910")]
    public class WMTSRequest : IServiceRequestInterpreter
    {
        private static IFormatProvider _nhi = System.Globalization.CultureInfo.InvariantCulture.NumberFormat;
        private IMapServer _mapServer = null;
        private static Guid _metaprovider = new Guid("D33D3DD2-DD63-4a47-9F84-F840FE0D01C0");
        private static byte[] _emptyPic = null;

        public WMTSRequest()
        {
            if(_emptyPic==null)
            {
                try
                {
                    using (var bm = new Bitmap(1, 1))
                    {
                        bm.MakeTransparent();

                        MemoryStream ms = new MemoryStream();
                        bm.Save(ms, ImageFormat.Png);
                        _emptyPic = ms.ToArray();
                    }
                }
                catch { }
            }
        }

        #region IServiceRequestInterpreter

        public InterpreterCapabilities Capabilities
        {
            get
            {
                return new InterpreterCapabilities(new InterpreterCapabilities.Capability[]{
                    new InterpreterCapabilities.LinkCapability("GetCapabilities","{onlineresource}VERSION=1.0.0&SERVICE=WMTS&REQUEST=GetCapabilities","1.0.0")
                }
              );
            }
        }

        public string IntentityName
        {
            get
            {
                return "wmts";
            }
        }

        public void OnCreate(IMapServer mapServer)
        {
            _mapServer = mapServer;
        }

        public void Request(IServiceRequestContext context)
        {
            if (context == null || context.ServiceRequest == null || context.ServiceMap == null)
                return;

            if (_mapServer == null)
            {
                context.ServiceRequest.Response = "<FATALERROR>MapServer Object is not available!</FATALERROR>";
                return;
            }

            TileServiceMetadata metadata = context.ServiceMap.MetadataProvider(_metaprovider) as TileServiceMetadata;
            if (metadata == null || metadata.Use == false)
            {
                context.ServiceRequest.Response = "<ERROR>Service is not used with Tile Service</ERROR>";
                return;
            }

            string service = context.ServiceRequest.Service;
            string request = context.ServiceRequest.Request;

            if (request.Contains("=")) // QueryString
            {
                QueryString queryString = new QueryString(request);
                if (queryString.HasValue("service", "wmts") && queryString.HasValue("request", "getcapabilities") && queryString.HasValue("version", "1.0.0"))
                {
                    WmtsCapabilities100(context, metadata);
                    return;
                }
            }

            string[] args = request.Split('/');

            if (args.Length == 7)
            {
                string cacheFormat = args[0].ToLower();
                if (args[1].ToLower() != "ul" &&
                    args[1].ToLower() != "ll")
                    throw new ArgumentException();

                int epsg = int.Parse(args[2]);
                string style = args[3].ToLower();
                double scale = GetScale(metadata, args[4]); // double.Parse(args[4].Replace(",", "."), _nhi);
                int row = int.Parse(args[5]);
                int col = int.Parse(args[6].Split('.')[0]);
                string format = ".png";
                if (args[6].ToLower().EndsWith(".jpg")) format = ".jpg";

                byte[] imageData = null;
                if (scale > 0)
                {
                    if (cacheFormat == "compact")
                    {
                        imageData = GetCompactTile(context, metadata, epsg, scale, row, col, format, (args[1].ToLower() == "ul" ? GridOrientation.UpperLeft : GridOrientation.LowerLeft));
                    }
                    else
                    {
                        imageData = GetTile(context, metadata, epsg, scale, row, col, format, (args[1].ToLower() == "ul" ? GridOrientation.UpperLeft : GridOrientation.LowerLeft));
                    }

                    if (style != "default")
                    {
                        ImageProcessingFilters filter;
                        if (Enum.TryParse<ImageProcessingFilters>(style, true, out filter))
                            imageData = ImageProcessing.ApplyFilter(imageData, filter);
                    }
                }

                context.ServiceRequest.Response = new MapServerResponse()
                {
                    Data = imageData ?? _emptyPic,
                    ContentType = "image/jpg",
                    Expires = DateTime.UtcNow.AddDays(7)
                }.ToString();
            }

            return;
        }

        private byte[] GetTile(IServiceRequestContext context, TileServiceMetadata metadata, int epsg, double scale, int row, int col, string format, GridOrientation orientation)
        {
            if (!metadata.EPSGCodes.Contains(epsg))
                throw new ArgumentException("Wrong epsg argument");

            //if (!metadata.Scales.Contains(scale))
            //    throw new ArgumentException("Wrong scale argument");
            scale = metadata.Scales.GetScale(scale);
            if (scale <= 0.0)
                throw new ArgumentException("Wrong scale argument");

            //IEnvelope bounds = metadata.GetEPSGEnvelope(epsg);
            //if (bounds == null || bounds.Width == 0.0 || bounds.Height == 0.0)
            //    throw new Exception("No bounds defined for EPSG:" + epsg);

            format = format.ToLower();
            if (format != ".png" && format != ".jpg")
                throw new Exception("Unsupported image format");
            if (format == ".png" && metadata.FormatPng == false)
                throw new Exception("Format image/png not supported");
            if (format == ".jpg" && metadata.FormatJpg == false)
                throw new Exception("Format image/jpeg no supported");

            string path = _mapServer.TileCachePath + @"\" + context.ServiceMap.Name + @"\_alllayers\" +
                TileServiceMetadata.TilePath(orientation, epsg, scale, row, col) + format;
            if ((orientation == GridOrientation.UpperLeft && metadata.UpperLeftCacheTiles) ||
                (orientation == GridOrientation.LowerLeft && metadata.LowerLeftCacheTiles))
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists)
                {
                    //context.ServiceRequest.Response = fi.FullName;
                    using (FileStream fs = File.Open(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)) //new FileStream(bundleFilename, FileMode.Open, FileAccess.Read))
                    {
                        byte[] data = new byte[fi.Length];
                        fs.Read(data, 0, data.Length);
                        return data;
                    }
                }
            }

            return null;
        }

        private byte[] GetCompactTile(IServiceRequestContext context, TileServiceMetadata metadata, int epsg, double scale, int row, int col, string format, GridOrientation orientation)
        {
            if (!metadata.EPSGCodes.Contains(epsg))
                throw new ArgumentException("Wrong epsg argument");

            if (orientation != GridOrientation.UpperLeft)
                throw new ArgumentException("Compact Tiles Orientation must bei Upper Left!");

            scale = metadata.Scales.GetScale(scale);
            if (scale <= 0.0)
                throw new ArgumentException("Wrong scale argument");

            //IEnvelope bounds = metadata.GetEGPSEnvelope(epsg);
            //if (bounds == null || bounds.Width == 0.0 || bounds.Height == 0.0)
            //    throw new Exception("No bounds defined for EPSG:" + epsg);
            IPoint origin = metadata.GetOriginUpperLeft(epsg);
            if (origin == null)
                throw new Exception("No origin defined for EPSG:" + epsg);

            format = format.ToLower();
            if (format != ".png" && format != ".jpg")
                throw new Exception("Unsupported image format");
            if (format == ".png" && metadata.FormatPng == false)
                throw new Exception("Format image/png not supported");
            if (format == ".jpg" && metadata.FormatJpg == false)
                throw new Exception("Format image/jpeg no supported");

            string path = _mapServer.TileCachePath + @"\" + context.ServiceMap.Name + @"\_alllayers\compact\" +
                TileServiceMetadata.ScalePath(orientation, epsg, scale);

            string compactTileName = CompactTileName(row, col);

            string bundleFilename = path + @"\" + compactTileName + ".tilebundle";
            string bundleDoneFilename = path + @"\" + compactTileName + ".tilebundle.done";
            string bundleCalcFilename = path + @"\" + compactTileName + ".tilebundle.calc";

            if (new FileInfo(bundleFilename).Exists)
            {
                return GetCompactTileBytes(context, path, row, col);
            }



            if (IsDirectoryEmpty(path))
            {
                #region On The Fly

                using (IServiceMap map = context.ServiceMap)
                {
                    ISpatialReference sRef = SpatialReference.FromID("epsg:" + epsg);

                    map.Display.SpatialReference = sRef;
                    map.Display.dpi = metadata.Dpi;

                    map.Display.iWidth = metadata.TileWidth;
                    map.Display.iHeight = metadata.TileHeight;

                    double res = (double)scale / (metadata.Dpi / 0.0254);
                    if (map.Display.MapUnits != GeoUnits.Meters)
                    {
                        GeoUnitConverter converter = new GeoUnitConverter();
                        res = converter.Convert(res, GeoUnits.Meters, map.Display.MapUnits);
                    }

                    origin = orientation == GridOrientation.UpperLeft ? metadata.GetOriginUpperLeft(epsg) : metadata.GetOriginLowerLeft(epsg);

                    double H = metadata.TileHeight * res;
                    double y = (orientation == GridOrientation.UpperLeft ?
                        origin.Y - H * (row + 1) :
                        origin.Y + H * row);

                    double W = metadata.TileWidth * res;
                    double x = origin.X + W * col;

                    map.Display.ZoomTo(new Envelope(x, y, x + W, y + H));
                    map.Render();

                    bool maketrans = map.Display.MakeTransparent;
                    map.Display.MakeTransparent = true;
                    MemoryStream ms = new MemoryStream();
                    map.SaveImage(ms, format == ".jpg" ? System.Drawing.Imaging.ImageFormat.Jpeg : System.Drawing.Imaging.ImageFormat.Png);
                    map.Display.MakeTransparent = maketrans;

                    return ms.ToArray();
                }

                #endregion

                #region Tile from Existing UpLevel Tiles (Vorteil Resampling wird nicht von Browser erledigt, ist meistens Fast -> hier Nearstneigbor)

                int level2 = metadata.Scales.IndexOf(scale);
                if (level2 <= 0)
                    return null;

                using (IServiceMap map = context.ServiceMap)
                {
                    double res = (double)scale / (metadata.Dpi / 0.0254);
                    if (map.Display.MapUnits != GeoUnits.Meters)
                    {
                        GeoUnitConverter converter = new GeoUnitConverter();
                        res = converter.Convert(res, GeoUnits.Meters, map.Display.MapUnits);
                    }

                    double H = metadata.TileHeight * res;
                    double y = origin.Y - H * (row + 1);

                    double W = metadata.TileWidth * res;
                    double x = origin.X + W * col;

                    while (true)
                    {
                        if (level2 <= 0)
                            break;

                        double scale2 = metadata.Scales[level2 - 1];

                        string path2 = _mapServer.TileCachePath + @"\" + context.ServiceMap.Name + @"\_alllayers\compact\" +
                                        TileServiceMetadata.ScalePath(orientation, epsg, scale2);
                        if (IsDirectoryEmpty(path2))
                        {
                            level2--;
                            continue;
                        }


                        double res2 = scale2 / (metadata.Dpi / 0.0254);

                        double W2 = metadata.TileWidth * res2;
                        double H2 = metadata.TileHeight * res2;

                        int col2_0 = (int)Math.Floor((x - origin.X) / W2);
                        int row2_0 = (int)Math.Floor((origin.Y - (y + H)) / H2);

                        int col2_1 = (int)Math.Floor((x + W - origin.X) / W2);
                        int row2_1 = (int)Math.Floor((origin.Y - y) / H2);

                        double x2_0 = origin.X + W2 * col2_0,
                               y2_0 = origin.Y - H2 * (row2_1 + 1);

                        double W20 = Math.Abs(col2_1 - col2_0 + 1) * W2,
                               H20 = Math.Abs(row2_1 - row2_0 + 1) * H2;

                        using (Bitmap bm = new Bitmap(Math.Abs(col2_1 - col2_0 + 1) * metadata.TileWidth, Math.Abs(row2_1 - row2_0 + 1) * metadata.TileHeight))
                        using (Graphics gr = Graphics.FromImage(bm))
                        {
                            for (int r2 = row2_0; r2 <= row2_1; r2++)
                            {
                                for (int c2 = col2_0; c2 <= col2_1; c2++)
                                {
                                    byte[] buffer = GetCompactTileBytes(context, path2, r2, c2);
                                    if (buffer != null && buffer.Length > 0)
                                    {
                                        MemoryStream ms = new MemoryStream(buffer);
                                        var tileImage = Image.FromStream(ms);
                                        gr.DrawImage(tileImage, new PointF((c2 - col2_0) * metadata.TileWidth, (r2 - row2_0) * metadata.TileHeight));
                                    }
                                }
                            }

                            float imageX = (float)((x - x2_0) / W20 * (double)bm.Width);
                            float imageY = bm.Height - (float)((y - y2_0) / H20 * (double)bm.Height);

                            float imageW = (float)((double)metadata.TileWidth * res / res2);
                            float imageH = (float)((double)metadata.TileHeight * res / res2);

                            using (Bitmap outputBm = new Bitmap(metadata.TileWidth, metadata.TileHeight))
                            using (Graphics outputGr = Graphics.FromImage(outputBm))
                            {
                                outputGr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;

                                outputGr.DrawImage(bm,
                                    new RectangleF(-.5f, -.5f, (float)outputBm.Width + 1f, (float)outputBm.Height + 1f),
                                    new RectangleF(imageX, imageY - imageH, imageW, imageH),
                                    GraphicsUnit.Pixel);


                                MemoryStream output = new MemoryStream();
                                outputBm.Save(output, format == ".png" ? ImageFormat.Png : ImageFormat.Jpeg);

                                return output.ToArray();
                            }
                        }
                    }
                }

                #endregion
            }


            return null;
        }

        private byte[] GetCompactTileBytes(IServiceRequestContext context, string path, int row, int col)
        {
            string compactTileName = CompactTileName(row, col);

            string bundleFilename = path + @"\" + compactTileName + ".tilebundle";
            string bundleIndexFilename = path + @"\" + compactTileName + ".tilebundlx";

            FileInfo fi = new FileInfo(bundleIndexFilename);
            if (!fi.Exists)
            {
                return null;
            }

            CompactTileIndex bundleIndex = new CompactTileIndex(bundleIndexFilename);

            int bundleStartRow = CompactTileStart(row);
            int bundleStartCol = CompactTileStart(col);

            try
            {

                int tileLength, tilePosition = bundleIndex.TilePosition(row - bundleStartRow, col - bundleStartCol, out tileLength);

                if (tilePosition < 0)
                    return null;

                using (FileStream fs = File.Open(bundleFilename, FileMode.Open, FileAccess.Read, FileShare.Read)) //new FileStream(bundleFilename, FileMode.Open, FileAccess.Read))
                {
                    fs.Position = tilePosition;

                    byte[] data = new byte[tileLength];
                    fs.Read(data, 0, tileLength);
                    return data;
                }
            }
            catch (Exception ex)
            {
                TileServiceMetadata metadata = context.ServiceMap.MetadataProvider(_metaprovider) as TileServiceMetadata;
                using (System.Drawing.Bitmap bm = new Bitmap(metadata.TileWidth, metadata.TileHeight))
                using (System.Drawing.Graphics gr = Graphics.FromImage(bm))
                using (System.Drawing.Font font = new Font("Arial", 9f))
                {
                    gr.DrawString(ex.Message, font, Brushes.Red, new RectangleF(0f, 0f, (float)bm.Width, (float)bm.Height));

                    MemoryStream ms = new MemoryStream();
                    bm.Save(ms, ImageFormat.Png);

                    return ms.ToArray();
                }
            }
        }


        #endregion

        private void WmtsCapabilities100(IServiceRequestContext context, TileServiceMetadata metadata)
        {
            gView.Framework.OGC.WMTS.Version_1_0_0.Capabilities capabilities = new Framework.OGC.WMTS.Version_1_0_0.Capabilities()
            {
                version = "1.0.0"
            };

            capabilities.NameSpaces = new System.Xml.Serialization.XmlSerializerNamespaces();
            capabilities.NameSpaces.Add("ows", "http://www.opengis.net/ows/1.1");
            capabilities.NameSpaces.Add("xlink", "http://www.w3.org/1999/xlink");
            capabilities.NameSpaces.Add("gml", "http://www.opengis.net/gml");

            #region ServiceIndentification

            capabilities.ServiceIdentification = new gView.Framework.OGC.WMTS.Version_1_0_0.ServiceIdentification();
            capabilities.ServiceIdentification.Title = new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType() {
                Value= context.ServiceMap.Name
            } };
            capabilities.ServiceIdentification.ServiceType = new gView.Framework.OGC.WMTS.Version_1_0_0.CodeType() { Value = "OGC WMTS" };
            capabilities.ServiceIdentification.ServiceTypeVersion = new string[] { "1.0.0" };

            #endregion

            string restFulUrl = context.ServiceRequest.OnlineResource.ToLower().Replace("/maprequest/wmts/", "/tilewmts/");

            #region OperationsMetadata

            capabilities.OperationsMetadata = new gView.Framework.OGC.WMTS.Version_1_0_0.OperationsMetadata();

            var getCapOperation = new gView.Framework.OGC.WMTS.Version_1_0_0.Operation() { name = "GetCapabilities" };
            getCapOperation.DCP = new gView.Framework.OGC.WMTS.Version_1_0_0.DCP[] { new gView.Framework.OGC.WMTS.Version_1_0_0.DCP() };
            getCapOperation.DCP[0].Item = new gView.Framework.OGC.WMTS.Version_1_0_0.HTTP();
            getCapOperation.DCP[0].Item.Items = new gView.Framework.OGC.WMTS.Version_1_0_0.RequestMethodType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.RequestMethodType() };

            getCapOperation.DCP[0].Item.Items[0].href = context.ServiceRequest.OnlineResource + "?SERVICE=WMTS&VERSION=1.0.0" + "&";
            getCapOperation.DCP[0].Item.Items[0].Constraint = new gView.Framework.OGC.WMTS.Version_1_0_0.DomainType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.DomainType() };
            getCapOperation.DCP[0].Item.Items[0].Constraint[0].name = "GetEncoding";
            getCapOperation.DCP[0].Item.Items[0].Constraint[0].AllowedValues = new object[] { new gView.Framework.OGC.WMTS.Version_1_0_0.ValueType() { Value = "KVP" /*"RESTful"*/ } };
            getCapOperation.DCP[0].Item.ItemsElementName = new gView.Framework.OGC.WMTS.Version_1_0_0.ItemsChoiceType[] { gView.Framework.OGC.WMTS.Version_1_0_0.ItemsChoiceType.Get };


            var getTileOperation = new gView.Framework.OGC.WMTS.Version_1_0_0.Operation() { name = "GetTile" };
            getTileOperation.DCP = new gView.Framework.OGC.WMTS.Version_1_0_0.DCP[] { new gView.Framework.OGC.WMTS.Version_1_0_0.DCP() };
            getTileOperation.DCP[0].Item = new gView.Framework.OGC.WMTS.Version_1_0_0.HTTP();
            getTileOperation.DCP[0].Item.Items = new gView.Framework.OGC.WMTS.Version_1_0_0.RequestMethodType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.RequestMethodType() };

            getTileOperation.DCP[0].Item.Items[0].href = restFulUrl;
            getTileOperation.DCP[0].Item.Items[0].Constraint = new gView.Framework.OGC.WMTS.Version_1_0_0.DomainType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.DomainType() };
            getTileOperation.DCP[0].Item.Items[0].Constraint[0].name = "GetEncoding";
            getTileOperation.DCP[0].Item.Items[0].Constraint[0].AllowedValues = new object[] { new gView.Framework.OGC.WMTS.Version_1_0_0.ValueType() { Value = "RESTful" } };
            getTileOperation.DCP[0].Item.ItemsElementName = new gView.Framework.OGC.WMTS.Version_1_0_0.ItemsChoiceType[] { gView.Framework.OGC.WMTS.Version_1_0_0.ItemsChoiceType.Get };

            capabilities.OperationsMetadata.Operation = new gView.Framework.OGC.WMTS.Version_1_0_0.Operation[]
            {
                getCapOperation, getTileOperation
            };

            #endregion

            #region Contents

            capabilities.Contents = new gView.Framework.OGC.WMTS.Version_1_0_0.ContentsType();

            List<gView.Framework.OGC.WMTS.Version_1_0_0.LayerType> layers = new List<gView.Framework.OGC.WMTS.Version_1_0_0.LayerType>();
            List<gView.Framework.OGC.WMTS.Version_1_0_0.TileMatrixSet> matrixSets = new List<gView.Framework.OGC.WMTS.Version_1_0_0.TileMatrixSet>();

            ISpatialReference sRef4326 = SpatialReference.FromID("epsg:4326");

            foreach (var epsg in metadata.EPSGCodes)
            {
                IEnvelope extent = metadata.GetEPSGEnvelope(epsg);
                if (extent == null)
                    continue;
                IPoint origin = metadata.GetOriginUpperLeft(epsg);
                if (origin == null)
                    continue;

                ISpatialReference sRef = SpatialReference.FromID("epsg:" + epsg);
                IEnvelope extent4326 = GeometricTransformer.Transform2D(extent, sRef, sRef4326).Envelope;

                if (double.IsInfinity(extent4326.minx)) extent4326.minx = -180D;
                if (double.IsInfinity(extent4326.miny)) extent4326.miny = -90D;
                if (double.IsInfinity(extent4326.maxx)) extent4326.maxx = 180D;
                if (double.IsInfinity(extent4326.maxy)) extent4326.maxy = 90D;

                foreach (string cacheType in new string[] { "classic", "compact" })
                {
                    string epsgPath = _mapServer.TileCachePath + @"\" + context.ServiceMap.Name + @"\_alllayers\" + (cacheType == "compact" ? @"compact\" : "") +
                        TileServiceMetadata.EpsgPath(GridOrientation.UpperLeft, epsg);

                    if (!new DirectoryInfo(epsgPath).Exists)
                        continue;

                    #region Layer

                    string layerName = context.ServiceMap.Name + " EPSG:" + epsg + " " + cacheType;
                    string layerId = context.ServiceMap.Name.ToLower().Replace(" ", "_") + "_" + epsg + "_" + cacheType;

                    var layer = new gView.Framework.OGC.WMTS.Version_1_0_0.LayerType();

                    layer.Title = new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType() { Value = layerName } };
                    layer.Identifier = new gView.Framework.OGC.WMTS.Version_1_0_0.CodeType() { Value = layerId };

                    List<gView.Framework.OGC.WMTS.Version_1_0_0.Style> styles = new List<Framework.OGC.WMTS.Version_1_0_0.Style>();
                    //styles.Add(new gView.Framework.OGC.WMTS.Version_1_0_0.Style()
                    //{
                    //    Title = new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType() { Value = "Default Style" } },
                    //    Identifier = new gView.Framework.OGC.WMTS.Version_1_0_0.CodeType() { Value = "default" }
                    //});
                    foreach(var styleVal in Enum.GetValues(typeof(ImageProcessingFilters)))
                    {
                        string name = Enum.GetName(typeof(ImageProcessingFilters), styleVal);
                        styles.Add(new Framework.OGC.WMTS.Version_1_0_0.Style()
                        {
                            Title = new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType() { Value = name } },
                            Identifier = new gView.Framework.OGC.WMTS.Version_1_0_0.CodeType() { Value = name.ToLower() }
                        });
                    }

                    layer.Style = styles.ToArray();

                    #region BoundingBox

                    layer.BoundingBox = new gView.Framework.OGC.WMTS.Version_1_0_0.BoundingBoxType[]
                    {
                        new gView.Framework.OGC.WMTS.Version_1_0_0.BoundingBoxType()
                        {
                            crs="urn:ogc:def:crs:EPSG::"+epsg,
                            LowerCorner=PointToString(extent.LowerLeft, sRef),
                            UpperCorner=PointToString(extent.UpperRight, sRef)
                        }
                    };
                    layer.WGS84BoundingBox = new gView.Framework.OGC.WMTS.Version_1_0_0.WGS84BoundingBoxType[]
                    {
                        new gView.Framework.OGC.WMTS.Version_1_0_0.WGS84BoundingBoxType()
                        {
                            crs="urn:ogc:def:crs:OGC:2:84",  // urn:ogc:def:crs:OGC:2:84
                            LowerCorner=PointToString(extent4326.LowerLeft, sRef4326),
                            UpperCorner=PointToString(extent4326.UpperRight, sRef4326)
                        }
                    };

                    #endregion

                    layer.TileMatrixSetLink = new gView.Framework.OGC.WMTS.Version_1_0_0.TileMatrixSetLink[]
                    {
                        new gView.Framework.OGC.WMTS.Version_1_0_0.TileMatrixSetLink()
                        {
                            TileMatrixSet=layerId+"_default_matrixset"
                        }
                    };

                    List<string> formats = new List<string>();
                    if (metadata.FormatJpg) formats.Add("image/jpg");
                    if (metadata.FormatPng) formats.Add("image/png");
                    layer.Format = formats.ToArray();

                    List<Framework.OGC.WMTS.Version_1_0_0.URLTemplateType> resourceURLs = new List<Framework.OGC.WMTS.Version_1_0_0.URLTemplateType>();
                    if (metadata.FormatJpg)
                    {
                        resourceURLs.Add(new Framework.OGC.WMTS.Version_1_0_0.URLTemplateType()
                        {
                            resourceType = Framework.OGC.WMTS.Version_1_0_0.URLTemplateTypeResourceType.tile,
                            format = "image/jpg",
                            template = restFulUrl + "/" + cacheType + "/ul/" + epsg + "/{Style}/{TileMatrix}/{TileRow}/{TileCol}.jpg"
                        });
                    }
                    if (metadata.FormatPng)
                    {
                        resourceURLs.Add(new Framework.OGC.WMTS.Version_1_0_0.URLTemplateType()
                        {
                            resourceType = Framework.OGC.WMTS.Version_1_0_0.URLTemplateTypeResourceType.tile,
                            format = "image/png",
                            template = restFulUrl + "/" + cacheType + "/ul/" + epsg + "/{Style}/{TileMatrix}/{TileRow}/{TileCol}.png"
                        });
                    }
                    layer.ResourceURL = resourceURLs.ToArray();

                    layers.Add(layer);

                    #endregion

                    #region Matrix Set

                    double matrixSetWidth = extent.Width;
                    double matrixSetHeight = extent.Height;

                    var matrixSet = new gView.Framework.OGC.WMTS.Version_1_0_0.TileMatrixSet();

                    matrixSet.Title = new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType[] { new gView.Framework.OGC.WMTS.Version_1_0_0.LanguageStringType() { Value = layerName + " Default Matrix Set" } };
                    matrixSet.Identifier = new gView.Framework.OGC.WMTS.Version_1_0_0.CodeType() { Value = layerId + "_default_matrixset" };
                    matrixSet.SupportedCRS = "urn:ogc:def:crs:EPSG::" + epsg;

                    matrixSet.TileMatrix = new gView.Framework.OGC.WMTS.Version_1_0_0.TileMatrix[metadata.Scales.Count];

                    #region DPI

                    double inchMeter = 0.0254; /* 0.0254000508001016;*/
                    double dpi = inchMeter * 1000D / 0.28D;   // wmts 0.28mm -> 1 Pixel;
                    double dpm = dpi / inchMeter;

                    #endregion

                    for (int s = 0, to = metadata.Scales.Count; s < to; s++)
                    {
                        string scalePath = _mapServer.TileCachePath + @"\" + context.ServiceMap.Name + @"\_alllayers\" + (cacheType == "compact" ? @"compact\" : "") +
                            TileServiceMetadata.ScalePath(GridOrientation.UpperLeft, epsg, metadata.Scales[s]);

                        if (!new DirectoryInfo(scalePath).Exists)
                            break;

                        double resolution = metadata.Scales[s] / (metadata.Dpi / inchMeter);

                        matrixSet.TileMatrix[s] = new gView.Framework.OGC.WMTS.Version_1_0_0.TileMatrix();
                        matrixSet.TileMatrix[s].Identifier = new gView.Framework.OGC.WMTS.Version_1_0_0.CodeType() { Value = s.ToString() };
                        matrixSet.TileMatrix[s].TopLeftCorner = PointToString(origin, sRef);
                        matrixSet.TileMatrix[s].TileWidth = metadata.TileWidth.ToString();
                        matrixSet.TileMatrix[s].TileHeight = metadata.TileHeight.ToString();

                        double tileWidth = metadata.TileWidth * resolution;
                        double tileHeight = metadata.TileHeight * resolution;

                        int matrixWidth = (int)Math.Round(matrixSetWidth / tileWidth + 0.5);
                        int matrixHeight = (int)Math.Round(matrixSetHeight / tileHeight + 0.5);

                        matrixSet.TileMatrix[s].MatrixWidth = matrixWidth.ToString();
                        matrixSet.TileMatrix[s].MatrixHeight = matrixHeight.ToString();
                        matrixSet.TileMatrix[s].ScaleDenominator = resolution * dpm;
                    }

                    matrixSets.Add(matrixSet);

                    #endregion
                }
            }

            capabilities.Contents.DatasetDescriptionSummary = layers.ToArray();
            capabilities.Contents.TileMatrixSet = matrixSets.ToArray();

            #endregion

            XsdSchemaSerializer<gView.Framework.OGC.WMTS.Version_1_0_0.Capabilities> ser = new XsdSchemaSerializer<gView.Framework.OGC.WMTS.Version_1_0_0.Capabilities>();
            string xml = ser.Serialize(capabilities, null);

            xml = xml.Replace(@"<ows:DatasetDescriptionSummary xsi:type=""LayerType"">", "<Layer>");
            xml = xml.Replace(@"</ows:DatasetDescriptionSummary>", "</Layer>");

            context.ServiceRequest.Response = xml;
        }

        #region Helper

        #region Compact Helper

        public int CompactTileStart(int index)
        {
            if (index < 0)
            {
                return 0;
            }

            return (index >> 7) * 128;   // 128 x 128 Tiles werden zu einem Bundle zusammengefasst
        }

        public string CompactTileName(int row, int col)
        {
            return "R" + CompactTileStart(row).ToString("X8") + "C" + CompactTileStart(col).ToString("X8");
        }

        #endregion

        private bool IsDirectoryEmpty(string path)
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }

        private string PointToString(gView.Framework.Geometry.IPoint p, ISpatialReference sRef)
        {
            if (sRef != null &&
                (sRef.Gml3AxisX == AxisDirection.North || sRef.Gml3AxisX == AxisDirection.South) &&
                (sRef.Gml3AxisY == AxisDirection.West || sRef.Gml3AxisY == AxisDirection.East))
            {
                return p.Y.ToString(_nhi) + " " + p.X.ToString(_nhi);
            }
            else
            {
                return p.X.ToString(_nhi) + " " + p.Y.ToString(_nhi);
            }
        }

        private double GetScale(TileServiceMetadata metadata, string scaleArgument)
        {
            if (scaleArgument.StartsWith("~"))
            {
                scaleArgument = scaleArgument.Substring(1);
                int index = int.Parse(scaleArgument);
                if (index < 0 || index >= metadata.Scales.Count())
                    return 0D;

                return metadata.Scales[int.Parse(scaleArgument)];
            }
            return double.Parse(scaleArgument.Replace(",", "."), _nhi);
        }


        #endregion

        #region Classes

        public class QueryString
        {
            NameValueCollection nvc = new NameValueCollection();

            public QueryString(string queryString)
            {
                queryString = queryString.ToLower();

                foreach (string argument in queryString.Split('&'))
                {
                    int pos = argument.IndexOf("=");
                    if (pos > 0)
                        nvc.Add(argument.Substring(0, pos), argument.Substring(pos + 1));
                    else
                        nvc.Add(argument, String.Empty);
                }
            }

            public string GetValue(string key)
            {
                return nvc[key.ToLower()];
            }

            public bool HasValue(string key, string val)
            {
                return nvc[key.ToLower()] == val.ToLower();
            }
        }

        public class CompactTileIndex
        {
            public CompactTileIndex(string filename)
            {
                this.Filename = filename;
            }

            public string Filename { get; private set; }

            public int TilePosition(int row, int col, out int tileLength)
            {
                if (row < 0 || row > 128 || col < 0 || col > 128)
                    throw new ArgumentException("Compact Tile Index out of range");

                int indexPosition = ((row * 128) + col) * 8;

                using (FileStream fs = File.Open(this.Filename, FileMode.Open, FileAccess.Read, FileShare.Read)) // new FileStream(this.Filename, FileMode.Open, FileAccess.Read))
                {
                    byte[] data = new byte[8];
                    fs.Position = indexPosition;
                    fs.Read(data, 0, 8);

                    int position = BitConverter.ToInt32(data, 0);
                    tileLength = BitConverter.ToInt32(data, 4);

                    return position;
                }
            }
        }

        #endregion
    }
}
