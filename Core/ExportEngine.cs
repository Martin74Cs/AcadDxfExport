using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadDxfExport.Core
{
    public static class ExportEngine
    {
        /// <summary>
        /// Vyhledá v modelovém prostoru všechny názvy bloků, které jsou zde vloženy.
        /// </summary>
        public static List<string> GetBlockNamesInModelSpace(Database db)
        {
            var blockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                var modelSpace = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForRead);

                foreach (ObjectId entId in modelSpace)
                {
                    var ent = tr.GetObject(entId, OpenMode.ForRead);
                    if (ent is BlockReference blockRef)
                    {
                        var dynamicBtrId = blockRef.DynamicBlockTableRecord;
                        var btr = (BlockTableRecord)tr.GetObject(dynamicBtrId, OpenMode.ForRead);
                        blockNames.Add(btr.Name);
                    }
                }

                tr.Commit();
            }

            var list = new List<string>(blockNames);
            list.Sort();
            return list;
        }

        /// <summary>
        /// Vrátí seznam tagů všech definic atributů nalezených v jakémkoliv bloku v databázi.
        /// </summary>
        public static List<string> GetAllAttributeTags(Database db)
        {
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                foreach (ObjectId btrId in blockTable)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);

                    // Přeskakujeme layouts
                    if (btr.IsLayout)
                        continue;

                    foreach (ObjectId entId in btr)
                    {
                        var ent = tr.GetObject(entId, OpenMode.ForRead);
                        if (ent is AttributeDefinition attDef)
                        {
                            tags.Add(attDef.Tag);
                        }
                    }
                }

                tr.Commit();
            }

            var list = new List<string>(tags);
            list.Sort();
            return list;
        }

        /// <summary>
        /// Pomocná třída pro držení dat k exportu jednoho rámečku.
        /// </summary>
        private class FrameExportData
        {
            public string FileName { get; set; }
            public ObjectIdCollection IdsToClone { get; set; }
            public Extents3d Extents { get; set; }
        }

        /// <summary>
        /// Provede export všech instancí vybraného bloku rámečku z modelového prostoru do samostatných DXF souborů.
        /// </summary>
        public static void ExportFrames(
            Database db,
            string blockName,
            string tagAttributeName1,
            string tagAttributeName2,
            string outputFolder,
            bool translateToOrigin,
            Action<string> logCallback,
            Action<int, int> progressCallback)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrEmpty(blockName)) throw new ArgumentException("Název bloku nesmí být prázdný.", nameof(blockName));
            if (string.IsNullOrEmpty(tagAttributeName1)) throw new ArgumentException("Primární název tagu atributu nesmí být prázdný.", nameof(tagAttributeName1));
            if (string.IsNullOrEmpty(outputFolder)) throw new ArgumentException("Cílová složka nesmí být prázdná.", nameof(outputFolder));

            // Zajištění existence cílové složky
            Directory.CreateDirectory(outputFolder);

            var exportQueue = new List<FrameExportData>();

            // 1. fáze: Vyhledání rámečků a objektů k exportu uvnitř transakce (pouze pro čtení)
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                var modelSpace = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForRead);

                logCallback("Vyhledávání instancí rámečků v Modelovém prostoru...");

                var frames = new List<FrameInfo>();

                foreach (ObjectId entId in modelSpace)
                {
                    var ent = tr.GetObject(entId, OpenMode.ForRead);
                    if (ent is BlockReference blockRef)
                    {
                        var dynamicBtrId = blockRef.DynamicBlockTableRecord;
                        var btr = (BlockTableRecord)tr.GetObject(dynamicBtrId, OpenMode.ForRead);

                        if (string.Equals(btr.Name, blockName, StringComparison.OrdinalIgnoreCase))
                        {
                            Extents3d extents;
                            try
                            {
                                extents = blockRef.GeometricExtents;
                            }
                            catch (Exception ex)
                            {
                                logCallback($"Varování: Nelze získat hranice bloku (Handle: {blockRef.Handle}). Chyba: {ex.Message}. Tento rámeček bude přeskočen.");
                                continue;
                            }

                            // Hledání hodnot atributů pro sestavení názvu souboru
                            string val1 = FindAttributeValueForFrame(tr, modelSpace, blockRef, extents, tagAttributeName1);
                            string val2 = FindAttributeValueForFrame(tr, modelSpace, blockRef, extents, tagAttributeName2);

                            string fileName = null;
                            if (!string.IsNullOrEmpty(val1) && !string.IsNullOrEmpty(val2))
                            {
                                fileName = val1 + "_" + val2;
                            }
                            else if (!string.IsNullOrEmpty(val1))
                            {
                                fileName = val1;
                            }
                            else if (!string.IsNullOrEmpty(val2))
                            {
                                fileName = val2;
                            }

                            // Sanotizace názvu souboru
                            if (string.IsNullOrEmpty(fileName))
                            {
                                fileName = $"Ramecek_{blockRef.Handle}";
                            }
                            else
                            {
                                foreach (char c in Path.GetInvalidFileNameChars())
                                {
                                    fileName = fileName.Replace(c, '_');
                                }
                            }

                            frames.Add(new FrameInfo
                            {
                                BlockRefId = blockRef.Id,
                                Extents = extents,
                                FileName = fileName
                            });
                        }
                    }
                }

                logCallback($"Nalezeno {frames.Count} platných instancí bloku '{blockName}'. Vyhledávání objektů uvnitř hranic...");

                // 2. Pro každý rámeček vyhledáme všechny objekty ležící uvnitř jeho hranic
                foreach (var frame in frames)
                {
                    var idsToClone = new ObjectIdCollection  {
                        frame.BlockRefId // Rámeček samotný
                    };

                    foreach (ObjectId entId in modelSpace)
                    {
                        if (entId == frame.BlockRefId)
                            continue;

                        var ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                        if (ent == null || ent is Viewport)
                            continue;

                        // Použijeme inteligentní filtr IsInsideFrame namísto prostého Overlaps2D
                        if (IsInsideFrame(ent, frame.Extents))
                        {
                            idsToClone.Add(entId);
                        }
                    }

                    exportQueue.Add(new FrameExportData
                    {
                        FileName = frame.FileName,
                        IdsToClone = idsToClone,
                        Extents = frame.Extents
                    });
                }

                tr.Commit();
            }

            // 2. fáze: Samotný export (mimo transakci zdrojové databáze)
            int currentCount = 0;
            int totalCount = exportQueue.Count;
            progressCallback(currentCount, totalCount);

            foreach (var item in exportQueue)
            {
                currentCount++;
                logCallback($"[{currentCount}/{totalCount}] Exportuji: {item.FileName}.dxf");

                try
                {
                    // Použijeme db.Wblock, což je oficiální AutoCAD API pro klonování
                    // vybrané sady objektů do nového výkresu včetně všech závislostí,
                    // stylů a definic bloků (vyřeší chybějící rámečky a razítka).
                    using (var targetDb = db.Wblock(item.IdsToClone, Point3d.Origin))
                    {
                        // Posun na počátek (0,0,0)
                        if (translateToOrigin)
                        {
                            var translationVec = Point3d.Origin - item.Extents.MinPoint;
                            var transformMatrix = Matrix3d.Displacement(translationVec);

                            using (var targetTr = targetDb.TransactionManager.StartTransaction())
                            {
                                var targetModelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(targetDb);
                                var targetModelSpace = (BlockTableRecord)targetTr.GetObject(targetModelSpaceId, OpenMode.ForRead);

                                foreach (ObjectId id in targetModelSpace)
                                {
                                    var clonedEnt = targetTr.GetObject(id, OpenMode.ForWrite) as Entity;
                                    if (clonedEnt != null)
                                    {
                                        try
                                        {
                                            clonedEnt.TransformBy(transformMatrix);
                                        }
                                        catch
                                        {
                                            // Ignorujeme chyby transformace u specifických netransformovatelných entit
                                        }
                                    }
                                }
                                targetTr.Commit();
                            }
                        }

                        // Uložení databáze jako DXF
                        string dxfPath = Path.Combine(outputFolder, item.FileName + ".dxf");
                        targetDb.DxfOut(dxfPath, 16, DwgVersion.Current);
                    }
                }
                catch (Exception ex)
                {
                    logCallback($"Chyba: Rámeček '{item.FileName}' nebylo možné vyexportovat. Detaily chyby: {ex.Message}");
                }

                progressCallback(currentCount, totalCount);
            }

            logCallback("Export byl dokončen.");
        }

        /// <summary>
        /// Zkontroluje, zda se dva 3D hraniční kvádry překrývají ve 2D rovině (XY).
        /// </summary>
        private static bool Overlaps2D(Extents3d a, Extents3d b)
        {
            return !(a.MinPoint.X > b.MaxPoint.X ||
                     a.MaxPoint.X < b.MinPoint.X ||
                     a.MinPoint.Y > b.MaxPoint.Y ||
                     a.MaxPoint.Y < b.MinPoint.Y);
        }

        /// <summary>
        /// Inteligentně posoudí, zda entita leží uvnitř zadaného rámečku na základě jejího typu a geometrického středu.
        /// Zabraňuje začlenění okrajových čar dotýkajících se hranic rámečku zvnějšku.
        /// </summary>
        private static bool IsInsideFrame(Entity ent, Extents3d frameExt)
        {
            double tol = 0.1;
            double minX = frameExt.MinPoint.X - tol;
            double minY = frameExt.MinPoint.Y - tol;
            double maxX = frameExt.MaxPoint.X + tol;
            double maxY = frameExt.MaxPoint.Y + tol;

            Point3d testPoint;

            if (ent is BlockReference br)
            {
                // Pro bloky (včetně rámečků a razítek) je rozhodující jejich bod vložení
                testPoint = br.Position;
            }
            else if (ent is Line line)
            {
                // Pro čáru je rozhodující její střed, což odfiltruje propojovací čáry vedoucí ven z rámečku
                testPoint = new Point3d(
                    (line.StartPoint.X + line.EndPoint.X) / 2.0,
                    (line.StartPoint.Y + line.EndPoint.Y) / 2.0,
                    (line.StartPoint.Z + line.EndPoint.Z) / 2.0
                );
            }
            else if (ent is Circle circle)
            {
                testPoint = circle.Center;
            }
            else if (ent is Arc arc)
            {
                testPoint = arc.Center;
            }
            else if (ent is DBText text)
            {
                testPoint = text.Position;
            }
            else if (ent is MText mtext)
            {
                testPoint = mtext.Location;
            }
            else if (ent is Curve curve)
            {
                // Pro obecné křivky (např. polylajny) zkusíme vzít bod v polovině délky
                try
                {
                    double startParam = curve.StartParam;
                    double endParam = curve.EndParam;
                    testPoint = curve.GetPointAtParameter((startParam + endParam) / 2.0);
                }
                catch
                {
                    // Fallback na střed extents
                    try
                    {
                        var ext = curve.GeometricExtents;
                        testPoint = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                            (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0
                        );
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
            else
            {
                // Obecný fallback pro ostatní entity: střed jejich bounding boxu
                try
                {
                    var ext = ent.GeometricExtents;
                    testPoint = new Point3d(
                        (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                        (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                        (ext.MinPoint.Z + ext.MaxPoint.Z) / 2.0
                    );
                }
                catch
                {
                    return false;
                }
            }

            return (testPoint.X >= minX && testPoint.X <= maxX &&
                    testPoint.Y >= minY && testPoint.Y <= maxY);
        }

        /// <summary>
        /// Vyhledá hodnotu atributu pro zadaný rámeček (třístupňové hledání).
        /// </summary>
        private static string FindAttributeValueForFrame(
            Transaction tr,
            BlockTableRecord modelSpace,
            BlockReference frameRef,
            Extents3d frameExtents,
            string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.StartsWith("---") || tag.StartsWith("["))
                return null;

            // 1. Zkusíme přímo na bloku rámečku
            string val = GetAttributeValueFromBlock(tr, frameRef, tag);
            if (!string.IsNullOrEmpty(val))
                return val;

            // 2. Zkusíme v blocích, které jsou geometricky uvnitř rámečku
            foreach (ObjectId entId in modelSpace)
            {
                if (entId == frameRef.Id)
                    continue;

                var ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                if (ent is BlockReference innerBlockRef)
                {
                    try
                    {
                        if (Overlaps2D(innerBlockRef.GeometricExtents, frameExtents))
                        {
                            val = GetAttributeValueFromBlock(tr, innerBlockRef, tag);
                            if (!string.IsNullOrEmpty(val))
                                return val;
                        }
                    }
                    catch { }
                }
            }

            // 3. Zkusíme ve vnořených blocích definice bloku rámečku
            return GetAttributeValueFromNestedBlockDefinition(tr, frameRef, tag);
        }

        /// <summary>
        /// Získá hodnotu atributu s daným tagem z bloku.
        /// </summary>
        private static string GetAttributeValueFromBlock(Transaction tr, BlockReference blockRef, string tag)
        {
            foreach (ObjectId attId in blockRef.AttributeCollection)
            {
                var attRef = (AttributeReference)tr.GetObject(attId, OpenMode.ForRead);
                if (string.Equals(attRef.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return attRef.TextString.Trim();
                }
            }
            return null;
        }

        /// <summary>
        /// Vyhledá definici bloku a rekurzivně z ní získá hodnotu atributu z vnořených definic bloků.
        /// </summary>
        private static string GetAttributeValueFromNestedBlockDefinition(Transaction tr, BlockReference parentRef, string tag)
        {
            var btrId = parentRef.DynamicBlockTableRecord;
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            return SearchDefinitionForAttribute(tr, btr, tag);
        }

        /// <summary>
        /// Rekurzivně vyhledává definici bloku pro atributy ve vnořených blocích.
        /// </summary>
        private static string SearchDefinitionForAttribute(Transaction tr, BlockTableRecord btr, string tag)
        {
            foreach (ObjectId entId in btr)
            {
                var ent = tr.GetObject(entId, OpenMode.ForRead);
                if (ent is BlockReference nestedRef)
                {
                    string val = GetAttributeValueFromBlock(tr, nestedRef, tag);
                    if (!string.IsNullOrEmpty(val))
                        return val;

                    var nestedBtr = (BlockTableRecord)tr.GetObject(nestedRef.DynamicBlockTableRecord, OpenMode.ForRead);
                    val = SearchDefinitionForAttribute(tr, nestedBtr, tag);
                    if (!string.IsNullOrEmpty(val))
                        return val;
                }
            }
            return null;
        }

        /// <summary>
        /// Pomocná struktura pro držení informací o jednotlivých instancích rámečku.
        /// </summary>
        private struct FrameInfo
        {
            public ObjectId BlockRefId { get; set; }
            public Extents3d Extents { get; set; }
            public string FileName { get; set; }
        }
    }
}
