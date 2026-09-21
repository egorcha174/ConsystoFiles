// Consysto STEP/IGES mesher.
// Reads a STEP or IGES file with OpenCascade, tessellates every face and writes a triangle soup for the preview core.
// It runs as a separate process: a crash or a runaway file inside OCCT cannot take the file manager down, and the
// LGPL library stays a separate, replaceable set of DLLs.
//
// Usage:  Consysto.StepMesher.exe <input.step|stp|iges|igs> <output.csmesh>
// Output: "CSMESH1\0", uint32 triangle count, uint32 reserved, then 9 float32 per triangle (three corners, mm).
// Exit:   0 ok, 1 unexpected error, 2 usage, 3 unreadable input, 4 no geometry, 5 cannot write, 6 too many triangles.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <exception>
#include <string>
#include <utility>
#include <vector>

#include <BRepBndLib.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <algorithm>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <IGESControl_Reader.hxx>
#include <Poly_Triangulation.hxx>
#include <STEPControl_Reader.hxx>
#include <Standard_Failure.hxx>
#include <TopExp_Explorer.hxx>
#include <TopLoc_Location.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Shape.hxx>

namespace
{
    // The same cap as MeshBuilder in the C# core: a preview must not allocate gigabytes.
    constexpr std::size_t MaximumTriangles = 5000000;

    // Chord deviation relative to the model's diagonal, so a bolt and a whole furnace look equally smooth.
    constexpr double RelativeDeflection = 0.0008;
    constexpr double AngularDeflection = 0.5;
    // The smallest chord deviation the mesher accepts; anything at or below zero makes it throw.
    constexpr double MinimumDeflection = 1e-5;

    enum ExitCode : int
    {
        Ok = 0,
        Unexpected = 1,
        Usage = 2,
        Unreadable = 3,
        NoGeometry = 4,
        CannotWrite = 5,
        TooManyTriangles = 6,
    };

    // OCCT file APIs take UTF-8 on Windows, which keeps Cyrillic folder names intact.
    std::string ToUtf8(const std::wstring& text)
    {
        int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (size <= 1)
            return {};
        std::string result(static_cast<std::size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, result.data(), size, nullptr, nullptr);
        result.resize(static_cast<std::size_t>(size) - 1);
        return result;
    }

    bool EndsWithIgnoreCase(const std::wstring& text, const std::wstring& suffix)
    {
        return text.size() >= suffix.size()
            && _wcsicmp(text.c_str() + text.size() - suffix.size(), suffix.c_str()) == 0;
    }

    bool ReadShape(const std::wstring& input, TopoDS_Shape& shape)
    {
        const std::string path = ToUtf8(input);
        if (EndsWithIgnoreCase(input, L".igs") || EndsWithIgnoreCase(input, L".iges"))
        {
            IGESControl_Reader reader;
            if (reader.ReadFile(path.c_str()) != IFSelect_RetDone)
                return false;
            reader.TransferRoots();
            shape = reader.OneShape();
            return true;
        }

        STEPControl_Reader reader;
        if (reader.ReadFile(path.c_str()) != IFSelect_RetDone)
            return false;
        reader.TransferRoots();
        shape = reader.OneShape();
        return true;
    }

    // Collects three corners per triangle; returns false when the cap is exceeded.
    bool Triangulate(const TopoDS_Shape& shape, std::vector<float>& corners)
    {
        for (TopExp_Explorer explorer(shape, TopAbs_FACE); explorer.More(); explorer.Next())
        {
            const TopoDS_Face& face = TopoDS::Face(explorer.Current());
            TopLoc_Location location;
            const Handle(Poly_Triangulation)& triangulation = BRep_Tool::Triangulation(face, location);
            if (triangulation.IsNull())
                continue;

            const gp_Trsf& transform = location.Transformation();
            const bool reversed = face.Orientation() == TopAbs_REVERSED;
            for (int i = 1; i <= triangulation->NbTriangles(); ++i)
            {
                if (corners.size() / 9 >= MaximumTriangles)
                    return false;

                int n1 = 0, n2 = 0, n3 = 0;
                triangulation->Triangle(i).Get(n1, n2, n3);
                if (reversed)
                    std::swap(n2, n3);

                for (int node : { n1, n2, n3 })
                {
                    const gp_Pnt point = triangulation->Node(node).Transformed(transform);
                    corners.push_back(static_cast<float>(point.X()));
                    corners.push_back(static_cast<float>(point.Y()));
                    corners.push_back(static_cast<float>(point.Z()));
                }
            }
        }

        return true;
    }

    // Writes to a temporary file and renames it: the cache never sees a half-written mesh,
    // even when the preview pane and the thumbnail ask for the same file at once.
    bool WriteMesh(const std::wstring& output, const std::vector<float>& corners)
    {
        const std::wstring temporary = output + L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp";
        FILE* file = nullptr;
        if (_wfopen_s(&file, temporary.c_str(), L"wb") != 0 || file == nullptr)
            return false;

        const std::uint32_t header[2] = { static_cast<std::uint32_t>(corners.size() / 9), 0 };
        bool written = std::fwrite("CSMESH1\0", 1, 8, file) == 8
            && std::fwrite(header, sizeof(std::uint32_t), 2, file) == 2
            && std::fwrite(corners.data(), sizeof(float), corners.size(), file) == corners.size();
        written = std::fclose(file) == 0 && written;

        if (!written || !MoveFileExW(temporary.c_str(), output.c_str(), MOVEFILE_REPLACE_EXISTING))
        {
            DeleteFileW(temporary.c_str());
            return false;
        }

        return true;
    }
}

int wmain(int argc, wchar_t* argv[])
{
    if (argc != 3)
    {
        std::fwprintf(stderr, L"usage: Consysto.StepMesher.exe <input.step|stp|iges|igs> <output.csmesh>\n");
        return Usage;
    }

    try
    {
        TopoDS_Shape shape;
        if (!ReadShape(argv[1], shape))
            return Unreadable;
        if (shape.IsNull())
            return NoGeometry;

        Bnd_Box box;
        BRepBndLib::Add(shape, box);
        if (box.IsVoid())
            return NoGeometry;

        // A shape of no size at all — a single point, an empty solid converted from another format — would ask for a
        // chord deviation of zero, and the mesher refuses that outright instead of returning nothing.
        const double diagonal = std::sqrt(box.SquareExtent());
        const double deflection = std::max(diagonal * RelativeDeflection, MinimumDeflection);
        BRepMesh_IncrementalMesh mesher(shape, deflection, /*isRelative*/ false, AngularDeflection, /*isInParallel*/ true);

        std::vector<float> corners;
        if (!Triangulate(shape, corners))
            return TooManyTriangles;
        if (corners.empty())
            return NoGeometry;

        return WriteMesh(argv[2], corners) ? Ok : CannotWrite;
    }
    catch (const Standard_Failure& failure)
    {
        std::fprintf(stderr, "OCCT failure: %s\n", failure.what());
        return Unexpected;
    }
    catch (const std::exception& exception)
    {
        std::fprintf(stderr, "failure: %s\n", exception.what());
        return Unexpected;
    }
}
