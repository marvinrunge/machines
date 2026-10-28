/*
 * Classic model exporter: see exportapp.hpp.
 *
 * Usage: classic_export <game folder> <model list> <output folder>
 *   <game folder>  the purchased game (holds models.bin and models/texture4)
 *   <model list>   lines "MACHINE|CONSTRUCTION <type> <level> [<subtype>] [<weapon combo>]"
 *   <output folder> receives <race>/<model id>.json, plus textures.txt listing every texture used
 *
 * Built with -fno-access-control so it can read RenMesh's private vertex and triangle data.
 */
#include "exportapp.hpp"
#include "afx/castfns.hpp"

#include <cstdio>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <map>
#include <set>
#include <sstream>
#include <vector>
#include <unistd.h>
#include <direct.h>

#include "base/base.hpp"
#include "mathex/transf3d.hpp"
#include "mathex/point3d.hpp"
#include "render/render.hpp"
#include "render/display.hpp"
#include "render/device.hpp"
#include "render/texmgr.hpp"
#include "render/mesh.hpp"
#include "render/meshinst.hpp"
#include "render/material.hpp"
#include "render/matvec.hpp"
#include "render/texture.hpp"
#include "render/colour.hpp"
#include "render/scale.hpp"
#include "render/internal/vtxdata.hpp"
#include "render/internal/vtxmat.hpp"
#include "render/internal/fixptvec.hpp"
#include "render/ttfpoly.hpp"
#include "render/spinpoly.hpp"
#include "machphys/oreholo.hpp"
#include "machphys/plasmbol.hpp"
#include "machphys/pulseblb.hpp"
#include "machphys/missile.hpp"
#include "machphys/stingl.hpp"
#include "machphys/stingm.hpp"
#include "machphys/plasplat.hpp"
#include "machphys/plasaura.hpp"
#include "machphys/pulsplat.hpp"
#include "machphys/pulsaura.hpp"
#include "machphys/jetringm.hpp"
#include "machphys/sparks.hpp"
#include "machphys/crakfire.hpp"
#include "machphys/beewave.hpp"
#include "world4d/link.hpp"
#include "sim/manager.hpp"
#include "world4d/entyplan.hpp"
#include "mathex/vec2.hpp"
#include "render/internal/trigroup.hpp"
#include "world4d/root.hpp"
#include "world4d/generic.hpp"
#include "world4d/genecomp.hpp"
#include "world4d/composit.hpp"
#include "world4d/link.hpp"
#include "ctl/list.hpp"
#include "world4d/entity.hpp"
#include "world4d/scenemgr.hpp"
#include "world4d/manager.hpp"
#include "machphys/machphys.hpp"
#include "machphys/persist.hpp"
#include "machphys/preload.hpp"
#include "machphys/aggressr.hpp"
#include "machphys/administ.hpp"
#include "machphys/geolocat.hpp"
#include "machphys/spylocat.hpp"
#include "machphys/rescarr.hpp"
#include "machphys/apc.hpp"
#include "machphys/technici.hpp"
#include "machphys/construc.hpp"
#include "machphys/pod.hpp"
#include "machphys/hwlab.hpp"
#include "machphys/smelter.hpp"
#include "machphys/factory.hpp"
#include "machphys/missemp.hpp"
#include "machphys/garrison.hpp"
#include "machphys/mine.hpp"
#include "machphys/beacon.hpp"
#include "machphys/machine.hpp"
#include "machphys/attack.hpp"
#include "world4d/domain.hpp"
#include "world4d/genrepos.hpp"
#include "machphys/weapon.hpp"
#include "machphys/bolter.hpp"
#include "machphys/helix.hpp"
#include "machphys/private/otherper.hpp"
#include "machphys/lmine.hpp"
#include "machphys/debrisun.hpp"
#include "mathex/abox2d.hpp"
#include "machphys/radidisc.hpp"
#include "machphys/electro.hpp"
#include "machphys/halo.hpp"
#include "machphys/loadunde.hpp"
#include "machphys/loadover.hpp"
#include "machphys/loadligh.hpp"
#include "machphys/dblflame.hpp"
#include "machphys/beam.hpp"
#include "machphys/nukewave.hpp"
#include "machphys/mushtop.hpp"
#include "machphys/mushshaf.hpp"
#include "machphys/vsphere.hpp"
#include "machphys/bsphere.hpp"
#include "machphys/wsphere.hpp"
#include "machphys/spider.hpp"
#include "machphys/wheels.hpp"
#include "machphys/tracks.hpp"
#include "machphys/internal/machinei.hpp"
#include "machphys/internal/spideri.hpp"
#include "machphys/internal/wheelsi.hpp"
#include "machphys/internal/itrack.hpp"
#include "world4d/compplan.hpp"
#include "world4d/planentr.hpp"
#include "machphys/plansurf.hpp"
#include "machphys/terrtile.hpp"
#include "system/pathname.hpp"
#include "machlog/machlog.hpp"
#include "machlog/scenario.hpp"
#include "sound/snd.hpp"
#include "sound/soundmix.hpp"
#include "sound/sndparam.hpp"
#include "world4d/soundman.hpp"

namespace
{

// A turn-to-face polygon (RenTTFPolygon): a sprite around a centre, always facing the camera.
struct Sprite
{
    std::string texture;
    float diffuse[4];
    float emissive[3];
    float centre[3];
    float width, height, depthOffset;
    std::vector<float> uvs;
};

// A spin-to-face polygon: corners along (x) and across (y) its axis, which turns to face the camera.
struct Spinner
{
    std::string texture;
    float diffuse[4];
    float emissive[3];
    float base[3];
    float axis[3];
    std::vector<float> corners;
    std::vector<float> uvs;
};

// One material's worth of triangles, in model space (original axes: z up).
struct Bucket
{
    std::string texture;
    float diffuse[4];
    float ambient[3];
    float emissive[3];
    std::vector<float> positions;
    std::vector<float> normals;
    std::vector<float> uvs;
    std::vector<unsigned> indices;
    // Each vertex's diffuse colour: a vertex with a material of its own (terrain tiles shade their corners
    // that way) is lit by that rather than the bucket's. Written only when some vertex has one.
    std::vector<float> colours;
    bool ownColours = false;
};

class ModelWriter
{
public:
    ModelWriter() {}

    // Rig mode: geometry relative to `node`, leaving out the links of `rigRoot` (they are nodes of their own).
    ModelWriter(const W4dEntity* node, const W4dComposite* rigRoot, const std::set<const W4dEntity*>* ownNodes = NULL)
        : node_(node), rigRoot_(rigRoot), ownNodes_(ownNodes) {}

    void addEntity(const W4dEntity& entity, int depth = 0)
    {
        if (rigRoot_ != NULL and depth > 0)
        {
            const W4dLink* link = dynamic_cast<const W4dLink*>(&entity);
            if (link != NULL and link->pComposite() == rigRoot_)
                return;
            // Moving parts of other things (a weapon's spinning ball) are nodes of their own too.
            if (ownNodes_ != NULL and ownNodes_->count(&entity))
                return;
        }
        if (getenv("CLASSIC_EXPORT_TRACE"))
        {
            std::cerr << std::string(depth * 2, ' ') << "entity visible=" << entity.visible() << " lods=";
            for (int lod = 0; lod < 4; ++lod)
            {
                if (entity.hasMesh(lod))
                {
                    Ren::ConstMeshPtr m = entity.mesh(lod).mesh();
                    std::cerr << lod << ":" << m->nTriangles() << "t/" << m->nTTFPolygons() << "ttf/" << m->nLines() << "l ";
                }
            }
            std::cerr << "children=" << entity.children().size() << std::endl;
        }

        if (entity.visible() and entity.hasMesh(lod_))
            addMesh(entity);

        // A composite's parts (links) are not ordinary children: they form their own subtree. A rig's own
        // links become nodes of their own; the parts of things mounted on it (weapons) stay with their node.
        const W4dComposite* composite = dynamic_cast<const W4dComposite*>(&entity);
        if (composite != NULL and composite != rigRoot_)
        {
            const W4dComposite::W4dLinks& links = composite->links();
            for (size_t i = 0; i < links.size(); ++i)
                addEntity(*links[i], depth + 1);
        }

        const W4dEntity::W4dEntities& children = entity.children();
        for (W4dEntity::W4dEntities::const_iterator it = children.begin(); it != children.end(); ++it)
            addEntity(**it, depth + 1);
    }

    // Model-space transform: each local transform composed up the parent chain (links point at their
    // owning link or composite).
    static MexTransform3d modelTransform(const W4dEntity& entity)
    {
        if (entity.pParent() == NULL)
            return MexTransform3d();
        MexTransform3d result;
        modelTransform(*entity.pParent()).transform(entity.localTransform(), &result);
        return result;
    }

    // The transform from `entity` to its ancestor `to` (the local transforms in between, composed).
    static MexTransform3d relativeTransform(const W4dEntity& entity, const W4dEntity* to)
    {
        if (&entity == to or entity.pParent() == NULL)
            return MexTransform3d();
        MexTransform3d result;
        relativeTransform(*entity.pParent(), to).transform(entity.localTransform(), &result);
        return result;
    }

    void write(const std::string& path, const std::string& id) const
    {
        std::ofstream out(path.c_str());
        out << std::setprecision(6);
        out << "{\"id\":\"" << id << "\",";
        writeParts(out);
        out << "}\n";
    }

    // "parts":[...] — one entry per material.
    void writeParts(std::ostream& out) const
    {
        out << "\"parts\":[";
        bool firstBucket = true;
        for (std::map<std::string, Bucket>::const_iterator it = buckets_.begin(); it != buckets_.end(); ++it)
        {
            const Bucket& b = it->second;
            if (b.indices.empty())
                continue;
            out << (firstBucket ? "" : ",") << "\n{\"texture\":\"" << b.texture << "\",\"diffuse\":["
                << b.diffuse[0] << "," << b.diffuse[1] << "," << b.diffuse[2] << "," << b.diffuse[3] << "]"
                << ",\"ambient\":[" << b.ambient[0] << "," << b.ambient[1] << "," << b.ambient[2] << "]"
                << ",\"emissive\":[" << b.emissive[0] << "," << b.emissive[1] << "," << b.emissive[2] << "]";
            writeArray(out, "positions", b.positions);
            writeArray(out, "normals", b.normals);
            writeArray(out, "uvs", b.uvs);
            if (b.ownColours)
                writeArray(out, "colours", b.colours);
            out << ",\"indices\":[";
            for (size_t i = 0; i < b.indices.size(); ++i)
                out << (i ? "," : "") << b.indices[i];
            out << "]}";
            firstBucket = false;
        }
        out << "\n]";

        // Turn-to-face polygons: sprites that always face the camera (eyes, glows, leaves).
        out << ",\"sprites\":[";
        for (size_t i = 0; i < sprites_.size(); ++i)
        {
            const Sprite& s = sprites_[i];
            out << (i ? "," : "") << "\n{\"texture\":\"" << s.texture << "\",\"diffuse\":["
                << s.diffuse[0] << "," << s.diffuse[1] << "," << s.diffuse[2] << "," << s.diffuse[3] << "]"
                << ",\"emissive\":[" << s.emissive[0] << "," << s.emissive[1] << "," << s.emissive[2] << "]"
                << ",\"centre\":[" << s.centre[0] << "," << s.centre[1] << "," << s.centre[2] << "]"
                << ",\"width\":" << s.width << ",\"height\":" << s.height << ",\"depthOffset\":" << s.depthOffset;
            writeArray(out, "uvs", s.uvs);
            out << "}";
        }
        out << "\n]";

        // Spin-to-face polygons: shapes that turn about an axis to face the camera (barrels, pipes).
        out << ",\"spinners\":[";
        for (size_t i = 0; i < spinners_.size(); ++i)
        {
            const Spinner& s = spinners_[i];
            out << (i ? "," : "") << "\n{\"texture\":\"" << s.texture << "\",\"diffuse\":["
                << s.diffuse[0] << "," << s.diffuse[1] << "," << s.diffuse[2] << "," << s.diffuse[3] << "]"
                << ",\"emissive\":[" << s.emissive[0] << "," << s.emissive[1] << "," << s.emissive[2] << "]"
                << ",\"base\":[" << s.base[0] << "," << s.base[1] << "," << s.base[2] << "]"
                << ",\"axis\":[" << s.axis[0] << "," << s.axis[1] << "," << s.axis[2] << "]";
            writeArray(out, "corners", s.corners);
            writeArray(out, "uvs", s.uvs);
            out << "}";
        }
        out << "\n]";
    }

    const std::set<std::string>& textures() const { return textures_; }
    size_t triangleCount() const { return triangles_; }

private:
    template <class T>
    static void writeArray(std::ostream& out, const char* name, const std::vector<T>& values)
    {
        out << ",\"" << name << "\":[";
        for (size_t i = 0; i < values.size(); ++i)
            out << (i ? "," : "") << values[i];
        out << "]";
    }

    void addMesh(const W4dEntity& entity)
    {
        const RenMeshInstance& instance = entity.mesh(lod_);
        Ren::ConstMeshPtr mesh = instance.mesh();
        const RenIVertexData* vertices = mesh->vertices_;   // private: -fno-access-control
        if (vertices == NULL)
            return;

        std::map<Ren::VertexIdx, const RenMaterial*> vertexMaterials;
        if (const RenIVertexMaterials* map = vertices->materialMap())
            for (RenIVertexMaterials::const_iterator it = map->begin(); it != map->end(); ++it)
                vertexMaterials[(*it).index()] = &(*it).material();

        double scale = 1.0;
        if (not instance.scale().isUnity())
        {
            const RenUniformScale* uniform = dynamic_cast<const RenUniformScale*>(&instance.scale());
            if (uniform)
                scale = uniform->factor();
        }

        const MexTransform3d transform = rigRoot_ != NULL ? relativeTransform(entity, node_) : modelTransform(entity);
        const Ren::MaterialVecPtr& overrides = instance.materialVec();

        size_t group = 0;
        for (size_t g = 0; g < mesh->triangles_.size(); ++g, ++group)
        {
            const RenITriangleGroup* triangles = mesh->triangles_[g];
            // A mesh instance may replace its mesh's materials, e.g. with race colours.
            const RenMaterial& material = (overrides.isDefined() and group < overrides->size())
                ? (*overrides)[group]
                : triangles->material();

            Bucket& bucket = bucketFor(material);
            const RenColour& groupColour = material.diffuse();
            for (size_t t = 0; t < triangles->nTriangles(); ++t)
            {
                Ren::VertexIdx corner[3];
                triangles->triangle(t, &corner[0], &corner[1], &corner[2]);
                for (int c = 0; c < 3; ++c)
                {
                    const RenIVertex& v = (*vertices)[corner[c]];
                    MexPoint3d p(v.x * scale, v.y * scale, v.z * scale);
                    transform.transform(&p);
                    bucket.positions.push_back(p.x());
                    bucket.positions.push_back(p.y());
                    bucket.positions.push_back(p.z());
                    // The original lights each vertex by its own normal; some tiles are wound the other way,
                    // so normals worked out from the winding would face down.
                    // A vertex without a stored normal is written as (0, 0, 0).
                    MexVec3 n(0, 0, 0);
                    if (v.normalIndex < vertices->normals().size())
                    {
                        RenIVec3FixPtS0_7 stored = vertices->normals()[v.normalIndex];
                        stored.convertToMex(&n);
                        transform.transform(&n);
                    }
                    bucket.normals.push_back(n.x());
                    bucket.normals.push_back(n.y());
                    bucket.normals.push_back(n.z());
                    bucket.uvs.push_back(v.tu);
                    bucket.uvs.push_back(v.tv);
                    // RenIIlluminator::applyVertexMaterials: the vertex's own material replaces the group's.
                    std::map<Ren::VertexIdx, const RenMaterial*>::const_iterator own = vertexMaterials.find(corner[c]);
                    const RenColour& colour = own != vertexMaterials.end() ? own->second->diffuse() : groupColour;
                    bucket.ownColours = bucket.ownColours or own != vertexMaterials.end();
                    bucket.colours.push_back(colour.r());
                    bucket.colours.push_back(colour.g());
                    bucket.colours.push_back(colour.b());
                    bucket.indices.push_back(bucket.indices.size());
                }
                ++triangles_;
            }
        }

        // The mesh's turn-to-face polygons, placed like its vertices.
        for (int s = 0; s < mesh->nTTFPolygons(); ++s)
        {
            const RenTTFPolygon& ttf = mesh->TTFPolygon(s);
            Sprite sprite;
            MexPoint3d centre = ttf.centre();
            centre = MexPoint3d(centre.x() * scale, centre.y() * scale, centre.z() * scale);
            transform.transform(&centre);
            sprite.centre[0] = centre.x();
            sprite.centre[1] = centre.y();
            sprite.centre[2] = centre.z();
            if (ttf.isRectangle())
            {
                sprite.width = ttf.asRectangle().width() * scale;
                sprite.height = ttf.asRectangle().height() * scale;
            }
            else
            {
                sprite.width = sprite.height = 2 * ttf.radius() * scale;
            }
            sprite.depthOffset = ttf.depthOffset();
            const RenMaterial& material = ttf.material();
            if (not material.texture().isNull())
            {
                sprite.texture = material.texture().name();
                textures_.insert(sprite.texture);
            }
            sprite.diffuse[0] = material.diffuse().r();
            sprite.diffuse[1] = material.diffuse().g();
            sprite.diffuse[2] = material.diffuse().b();
            sprite.diffuse[3] = material.diffuse().a();
            sprite.emissive[0] = material.emissive().r();
            sprite.emissive[1] = material.emissive().g();
            sprite.emissive[2] = material.emissive().b();
            for (size_t v = 0; v < ttf.nVertices(); ++v)
            {
                sprite.uvs.push_back(ttf.uv(_STATIC_CAST(Ren::VertexIdx, v)).x());
                sprite.uvs.push_back(ttf.uv(_STATIC_CAST(Ren::VertexIdx, v)).y());
            }
            sprites_.push_back(sprite);
        }

        // The mesh's spin-to-face polygons (gun barrels, pipes): flat shapes that turn about their own axis to
        // face the camera (RenSpinTFPolygon), placed like its vertices. Their corners lie along the axis (x) and
        // across it (y).
        for (size_t s = 0; s < mesh->stfps_.size(); ++s)
        {
            const RenSpinTFPolygon& stf = *mesh->stfps_[s];
            Spinner spinner;
            MexPoint3d base = stf.spinAxis().base();
            base = MexPoint3d(base.x() * scale, base.y() * scale, base.z() * scale);
            transform.transform(&base);
            MexVec3 axis = stf.spinAxis().direction();
            transform.transform(&axis);
            spinner.base[0] = base.x();
            spinner.base[1] = base.y();
            spinner.base[2] = base.z();
            spinner.axis[0] = axis.x();
            spinner.axis[1] = axis.y();
            spinner.axis[2] = axis.z();
            const RenIVertexData& corners = *stf.vertices();
            for (size_t v = 0; v < corners.size(); ++v)
            {
                spinner.corners.push_back(corners[v].x * scale);
                spinner.corners.push_back(corners[v].y * scale);
                spinner.uvs.push_back(corners[v].tu);
                spinner.uvs.push_back(corners[v].tv);
            }
            const RenMaterial& material = stf.material();
            if (not material.texture().isNull())
            {
                spinner.texture = material.texture().name();
                textures_.insert(spinner.texture);
            }
            spinner.diffuse[0] = material.diffuse().r();
            spinner.diffuse[1] = material.diffuse().g();
            spinner.diffuse[2] = material.diffuse().b();
            spinner.diffuse[3] = material.diffuse().a();
            spinner.emissive[0] = material.emissive().r();
            spinner.emissive[1] = material.emissive().g();
            spinner.emissive[2] = material.emissive().b();
            spinners_.push_back(spinner);
        }
    }

    Bucket& bucketFor(const RenMaterial& material)
    {
        std::string texture;
        if (not material.texture().isNull())
            texture = material.texture().name();
        const RenColour& d = material.diffuse();
        const RenColour& a = material.ambient();
        const RenColour& e = material.emissive();

        std::ostringstream key;
        key << texture << "|" << d.r() << "," << d.g() << "," << d.b() << "," << d.a()
            << "|" << a.r() << "," << a.g() << "," << a.b() << "|" << e.r() << "," << e.g() << "," << e.b();
        Bucket& bucket = buckets_[key.str()];
        if (bucket.texture.empty() and bucket.indices.empty())
        {
            bucket.texture = texture;
            bucket.diffuse[0] = d.r();
            bucket.diffuse[1] = d.g();
            bucket.diffuse[2] = d.b();
            bucket.diffuse[3] = d.a();
            bucket.ambient[0] = a.r();
            bucket.ambient[1] = a.g();
            bucket.ambient[2] = a.b();
            bucket.emissive[0] = e.r();
            bucket.emissive[1] = e.g();
            bucket.emissive[2] = e.b();
        }
        if (not texture.empty())
            textures_.insert(texture);
        return bucket;
    }

    std::map<std::string, Bucket> buckets_;
    std::vector<Sprite> sprites_;
    std::vector<Spinner> spinners_;
    std::set<std::string> textures_;
    size_t triangles_ = 0;
    const W4dEntity* node_ = NULL;
    const std::set<const W4dEntity*>* ownNodes_ = NULL;
public:
    // Which level of detail is written: 0, the most detailed in the models, unless told otherwise.
    void lod(W4dLOD level) { lod_ = level; }
private:
    W4dLOD lod_ = 0;
    const W4dComposite* rigRoot_ = NULL;
};

void writeTransform(std::ostream& out, const MexTransform3d& t)
{
    const MexVec3 x = t.xBasis(), y = t.yBasis(), z = t.zBasis();
    const MexPoint3d p = t.position();
    out << "[" << x.x() << "," << x.y() << "," << x.z() << "," << y.x() << "," << y.y() << "," << y.z() << ","
        << z.x() << "," << z.y() << "," << z.z() << "," << p.x() << "," << p.y() << "," << p.z() << "]";
}

// "name":{"duration":d,"tracks":[{"node":i,"samples":[[12 floats], ...]}]} — a link animation sampled 20 times a
// second; each sample is the link's local transform at that time.
void writeAnimation(std::ostream& out, const char* name, const W4dCompositePlanPtr& plan,
                    const W4dComposite::W4dLinks& links, bool* first)
{
    if (not plan.isDefined())
        return;
    const PhysRelativeTime duration = plan->finishTime();
    const int samples = std::max(2, int(duration * 20 + 0.5) + 1);
    out << (*first ? "" : ",") << "\"" << name << "\":{\"duration\":" << duration << ",\"tracks\":[";
    *first = false;
    bool firstTrack = true;
    const W4dCompositePlan::Entries& entries = plan->entries();
    for (W4dCompositePlan::Entries::const_iterator it = entries.begin(); it != entries.end(); ++it)
    {
        int node = -1;
        for (size_t i = 0; i < links.size(); ++i)
            if (links[i]->id() == (*it)->id())
                node = int(i) + 1; // node 0 is the machine itself
        if (node < 0)
            continue;
        out << (firstTrack ? "" : ",") << "{\"node\":" << node << ",\"samples\":[";
        firstTrack = false;
        MexTransform3d last = links[node - 1]->localTransform();
        for (int s = 0; s < samples; ++s)
        {
            MexTransform3d at;
            uint obsolete = 0;
            if ((*it)->plan().transform(duration * s / (samples - 1), &at, &obsolete) == W4dEntityPlan::DEFINED)
                last = at;
            out << (s ? "," : "");
            writeTransform(out, last);
        }
        out << "]}";
    }
    out << "]}";
}

// "rig":{...}: the machine as a tree of nodes (the machine, then each link), each with its rest transform
// relative to its parent and its own geometry; the upper body's turning node; and how its locomotion moves:
// spider-leg cycles sampled from the original plans, wheel nodes and radius, or track nodes and texture speed.
// A building is written the same way, with no locomotion, plus how its parts move while it works.
void writeRig(std::ostream& out, W4dComposite& composite, std::set<std::string>* textures)
{
    MachPhysMachine* machine = dynamic_cast<MachPhysMachine*>(&composite);
    const W4dComposite::W4dLinks& links = composite.links();
    std::vector<const W4dEntity*> nodes(1, &composite);
    nodes.insert(nodes.end(), links.begin(), links.end());

    // Parts that move by themselves (spinning rotors, a weapon's turning ball) become nodes too, even when they
    // belong to something mounted on the machine rather than to its own links.
    std::set<const W4dEntity*> ownNodes(nodes.begin(), nodes.end());
    std::vector<const W4dEntity*> pending(nodes.begin(), nodes.end());
    while (not pending.empty())
    {
        const W4dEntity* entity = pending.back();
        pending.pop_back();
        std::vector<const W4dEntity*> inside(entity->children().begin(), entity->children().end());
        if (const W4dComposite* inner = dynamic_cast<const W4dComposite*>(entity))
            if (inner != &composite)
                inside.insert(inside.end(), inner->links().begin(), inner->links().end());
        for (size_t i = 0; i < inside.size(); ++i)
        {
            if (ownNodes.count(inside[i]))
                continue;
            if (inside[i]->hasPlan() and inside[i]->entityPlan().hasMotionPlan())
            {
                nodes.push_back(inside[i]);
                ownNodes.insert(inside[i]);
            }
            pending.push_back(inside[i]);
        }
    }
    const auto addNode = [&](const W4dEntity* entity) {
        if (entity != NULL and not ownNodes.count(entity))
        {
            nodes.push_back(entity);
            ownNodes.insert(entity);
        }
    };

    // What moves as a weapon fires: the part that recoils (MachPhysWeapon::recoil: the link named "recoil", or the
    // whole weapon when it has none), or the autocannon's barrels, which turn instead (MachPhysBolter::fire).
    std::vector<std::pair<const W4dEntity*, MachPhys::Mounting> > firing;
    if (MachPhysCanAttack* attacker = dynamic_cast<MachPhysCanAttack*>(&composite))
    {
        for (uint w = 0; w < attacker->nMountedWeapons(); ++w)
        {
            MachPhysWeapon& weapon = attacker->weapon(w);
            const W4dEntity* moving = NULL;
            MachPhysBolter* bolter = dynamic_cast<MachPhysBolter*>(&weapon);
            if (bolter != NULL and weapon.type() == MachPhys::AUTO_CANNON)
                moving = bolter->pTurnLink_;
            else if (weapon.recoilId_ == 1)
                moving = &weapon;
            else if (weapon.recoilId_ > 1)
                moving = weapon.links()[weapon.recoilId_ - 2];
            if (moving == NULL)
                continue;
            addNode(moving);
            firing.push_back(std::make_pair(moving, weapon.mounting()));
        }
    }

    // A building's interior (MachPhysConstruction::pInteriorDomain) is a domain of its own, which the original
    // drew only while the camera was inside it.
    const W4dEntity* interior = NULL;
    if (MachPhysConstruction* construction = dynamic_cast<MachPhysConstruction*>(&composite))
        if (construction->hasInterior())
            addNode(interior = construction->pInteriorDomain());

    const auto indexOf = [&](const W4dEntity* entity) {
        for (size_t i = 0; i < nodes.size(); ++i)
            if (nodes[i] == entity)
                return int(i);
        return -1;
    };
    // The nearest node above an entity.
    const auto nodeAbove = [&](const W4dEntity* entity) {
        for (const W4dEntity* above = entity->pParent(); above != NULL; above = above->pParent())
            if (indexOf(above) >= 0)
                return above;
        return _STATIC_CAST(const W4dEntity*, &composite);
    };

    out << "\"rig\":{\"nodes\":[";
    for (size_t i = 0; i < nodes.size(); ++i)
    {
        const W4dEntity* parentNode = i == 0 ? NULL : nodeAbove(nodes[i]);
        const int parent = i == 0 ? -1 : indexOf(parentNode);
        ModelWriter writer(nodes[i], &composite, &ownNodes);
        writer.addEntity(*nodes[i]);
        textures->insert(writer.textures().begin(), writer.textures().end());
        // Links keep their names from the model (a building's doors are found by them).
        out << (i ? "," : "") << "\n{\"name\":\"";
        if (nodes[i] == interior)
            out << "interior";
        else
            out << nodes[i]->name();
        out << "\",\"parent\":" << parent << ",\"transform\":";
        writeTransform(out, i == 0 ? MexTransform3d() : ModelWriter::relativeTransform(*nodes[i], parentNode));
        out << ",";
        writer.writeParts(out);
        out << "}";
    }
    // A tower's head (MachPhysMissileEmplacement's "body" link) turns to its target like a machine's upper body
    // (MachPhysTurnerTracker); its rate, MAX_ROTATION, comes from parmdata.
    MachPhysMissileEmplacement* tower = dynamic_cast<MachPhysMissileEmplacement*>(&composite);
    const W4dEntity* turning = machine != NULL ? machine->pImpl_->pUpperBodyTurnLink_ : tower != NULL ? tower->pTurnLink_ : NULL;
    out << "],\"upperBody\":" << (turning != NULL ? indexOf(turning) : -1);
    if (not firing.empty())
    {
        out << ",\"weapons\":[";
        for (size_t i = 0; i < firing.size(); ++i)
        {
            const MachPhys::Mounting mounting = firing[i].second;
            out << (i ? "," : "") << "{\"node\":" << indexOf(firing[i].first) << ",\"mounting\":\""
                << (mounting == MachPhys::LEFT ? "left" : mounting == MachPhys::RIGHT ? "right" : "top") << "\"}";
        }
        out << "]";
    }

    // How the parts move as their motion plans move them, sampled at 50 Hz over one period, found where the
    // motion comes back to where it began.
    const PhysAbsoluteTime now = SimManager::instance().currentTime();
    const auto same = [](const MexTransform3d& a, const MexTransform3d& b) {
        MexPoint3d pa = a.position(), pb = b.position();
        if (pa.euclidianDistance(pb) > 1e-3)
            return false;
        MexVec3 ax = a.xBasis(), bx = b.xBasis(), ay = a.yBasis(), by = b.yBasis();
        return ax.euclidianDistance(bx) < 1e-3 and ay.euclidianDistance(by) < 1e-3;
    };
    const auto writeMotion = [&](const char* name) {
        out << ",\"" << name << "\":[";
        bool firstTrack = true;
        for (size_t i = 1; i < nodes.size(); ++i)
        {
            if (not nodes[i]->hasPlan() or not nodes[i]->entityPlan().hasMotionPlan())
                continue;
            const W4dEntity* parentNode = nodeAbove(nodes[i]);
            const MexTransform3d above = ModelWriter::relativeTransform(*nodes[i]->pParent(), parentNode);
            const double step = 0.02;
            const int most = 600;
            std::vector<MexTransform3d> samples;
            for (int s = 0; s <= most; ++s)
            {
                MexTransform3d local = nodes[i]->localTransform(), at;
                uint obsolete = 0;
                nodes[i]->entityPlan().transform(now + s * step, &local, &obsolete);
                above.transform(local, &at);
                samples.push_back(at);
            }
            int period = most;
            for (int k = 5; k < most; ++k)
                if (same(samples[k], samples[0]) and same(samples[k + 1], samples[1]))
                {
                    period = k;
                    break;
                }
            bool moves = false;
            for (int s = 1; s <= period and not moves; ++s)
                moves = not same(samples[s], samples[0]);
            if (not moves)
                continue;
            out << (firstTrack ? "" : ",") << "\n{\"node\":" << i << ",\"duration\":" << period * step << ",\"samples\":[";
            firstTrack = false;
            for (int s = 0; s <= period; ++s)
            {
                out << (s ? "," : "");
                writeTransform(out, samples[s]);
            }
            out << "]}";
        }
        out << "]";
    };

    // Idle motion: what parts do on their own (rotors spin, hips bob, a weapon's ball turns).
    writeMotion("idle");

    // A building at work (MachPhysConstruction::doWorking): its working cycle, fans turning.
    if (MachPhysConstruction* construction = dynamic_cast<MachPhysConstruction*>(&composite))
    {
        construction->isWorking(true);
        writeMotion("working");
    }
    if (machine == NULL)
    {
        out << "}";
        return;
    }

    // First person looks out from the centre front of the faceplate (MachPhys1stPersonMachineDriver::cameraAttachment).
    if (machine->hasFacePlate())
    {
        W4dLink& faceplate = machine->facePlate();
        const MexAlignedBox3d& box = faceplate.boundingVolume();
        MexPoint3d eye = box.centroid();
        eye.x(box.maxCorner().x());
        out << ",\"faceplate\":" << indexOf(&faceplate) << ",\"eye\":[" << eye.x() << "," << eye.y() << "," << eye.z() << "]";
    }

    MachPhysLocomotionMethod& locomotion = machine->locomotionMethod();
    MachPhysSpiderLegs* legs = dynamic_cast<MachPhysSpiderLegs*>(&locomotion);
    // A constructor at work (MachPhysConstructor::startConstructing) plays its building cycle: the arms move.
    W4dCompositePlanPtr constructing;
    const bool constructs = dynamic_cast<MachPhysConstructor*>(machine) != NULL and machine->cycleAnims(&constructing);
    if (legs != NULL or constructs)
    {
        out << ",\"animations\":{";
        bool first = true;
        if (legs != NULL)
        {
            const MachPhysSpiderLegsImpl& impl = *legs->pImpl_;
            writeAnimation(out, "rest", impl.restingPlanPtr_, links, &first);
            writeAnimation(out, "walk", impl.walkingPlanPtr_, links, &first);
            writeAnimation(out, "turnLeft", impl.turningLeftPlanPtr_, links, &first);
            writeAnimation(out, "turnRight", impl.turningRightPlanPtr_, links, &first);
            writeAnimation(out, "startWalking", impl.startWalkingPlanPtr_, links, &first);
            writeAnimation(out, "stopWalking", impl.stopWalkingPlanPtr_, links, &first);
            // The Gorilla's punches: the weapon plays one of its machine's two plans (MachPhysPunchWeapon::createPunchBlast).
            W4dCompositePlanPtr punch;
            if (machine->findCompositePlan("PunchA", &punch))
                writeAnimation(out, "punchA", punch, links, &first);
            if (machine->findCompositePlan("PunchB", &punch))
                writeAnimation(out, "punchB", punch, links, &first);
        }
        if (constructs)
            writeAnimation(out, "construct", constructing, links, &first);
        out << "}";
    }
    if (MachPhysWheels* wheels = dynamic_cast<MachPhysWheels*>(&locomotion))
    {
        const MachPhysWheelsImpl& impl = *wheels->pImpl_;
        out << ",\"wheels\":{\"radius\":" << impl.wheelRadius_ << ",\"left\":[";
        for (size_t i = 0; i < impl.leftWheels_.size(); ++i)
            out << (i ? "," : "") << indexOf(impl.leftWheels_[i]);
        out << "],\"right\":[";
        for (size_t i = 0; i < impl.rightWheels_.size(); ++i)
            out << (i ? "," : "") << indexOf(impl.rightWheels_[i]);
        out << "]}";
    }
    else if (MachPhysTracks* tracks = dynamic_cast<MachPhysTracks*>(&locomotion))
    {
        out << ",\"tracks\":{\"repeatsPerMeter\":" << tracks->lTrack_->repeatsPerMeter_
            << ",\"left\":" << indexOf(tracks->lTrack_->leg_) << ",\"right\":" << indexOf(tracks->rTrack_->leg_) << "}";
    }
    out << "}";
}

const char* raceName(MachPhys::Race race)
{
    switch (race)
    {
        case MachPhys::RED: return "red";
        case MachPhys::BLUE: return "blue";
        case MachPhys::GREEN: return "green";
        default: return "yellow";
    }
}

// Builds the object for one list line, like the game would. Returns NULL for types it does not know.
W4dEntity* build(W4dEntity* root, const std::vector<std::string>& words, MachPhys::Race race)
{
    const std::string& kind = words[0];
    const std::string& type = words[1];
    const size_t level = atoi(words[2].c_str());
    const MexTransform3d at;
    // The hologram a found mineral site shows (MachLogOreHolograph), turning slowly in the race's colour.
    if (type == "ORE_HOLOGRAPH")
        return _NEW(MachPhysOreHolograph(root, W4dTransform3d(), race, 1, 1));
    const MachLog::ObjectType objectType = MachLogScenario::objectType(type);

    // After the level come an optional subtype and an optional weapon combo (L_..., R_..., LR_..., T_..., LRT_...).
    std::string sub, comboName;
    for (size_t i = 3; i < words.size(); ++i)
    {
        const std::string& w = words[i];
        const bool isCombo = w.compare(0, 2, "L_") == 0 or w.compare(0, 2, "R_") == 0 or w.compare(0, 2, "T_") == 0
            or w.compare(0, 3, "LR_") == 0 or w.compare(0, 4, "LRT_") == 0;
        (isCombo ? comboName : sub) = w;
    }
    const int subType = sub.empty() ? 0 : MachLogScenario::objectSubType(objectType, sub);
    const MachPhys::WeaponCombo combo = comboName.empty() ? MachPhys::N_WEAPON_COMBOS : MachLogScenario::weaponCombo(comboName);

    if (kind == "MACHINE")
    {
        if (type == "AGGRESSOR")
            return _NEW(MachPhysAggressor(root, at, MachPhys::AggressorSubType(subType), level, 1, race, combo));
        if (type == "ADMINISTRATOR")
            return _NEW(MachPhysAdministrator(root, at, MachPhys::AdministratorSubType(subType), level, 1, race, combo));
        if (type == "GEO_LOCATOR")
            return _NEW(MachPhysGeoLocator(root, at, level, 1, race));
        if (type == "SPY_LOCATOR")
            return _NEW(MachPhysSpyLocator(root, at, level, 1, race));
        if (type == "RESOURCE_CARRIER")
            return _NEW(MachPhysResourceCarrier(root, at, level, 1, race));
        if (type == "APC")
            return _NEW(MachPhysAPC(root, at, level, 1, race));
        if (type == "TECHNICIAN")
        {
            // Software ("brain") levels from machphys/levels.cpp: Engineers only exist at 3, Brainboxes at 5.
            const size_t brain = sub == "TECH_BOY" ? 3 : sub == "BRAIN_BOX" ? 5 : 1;
            return _NEW(MachPhysTechnician(root, at, MachPhys::TechnicianSubType(subType), level, brain, race));
        }
        if (type == "CONSTRUCTOR")
            return _NEW(MachPhysConstructor(root, at, MachPhys::ConstructorSubType(subType), level, 1, race));
    }
    else
    {
        if (type == "POD")
            return _NEW(MachPhysPod(root, at, level, race));
        if (type == "HARDWARE_LAB")
            return _NEW(MachPhysHardwareLab(root, at, MachPhys::HardwareLabSubType(subType), level, race));
        if (type == "SMELTER")
            return _NEW(MachPhysSmelter(root, at, level, race));
        if (type == "FACTORY")
            return _NEW(MachPhysFactory(root, at, MachPhys::FactorySubType(subType), level, race));
        if (type == "MISSILE_EMPLACEMENT")
            return _NEW(MachPhysMissileEmplacement(root, at, MachPhys::MissileEmplacementSubType(subType), level, race,
                combo == MachPhys::N_WEAPON_COMBOS ? MachPhys::LR_AUTO_CANNON_X2 : combo));
        if (type == "GARRISON")
            return _NEW(MachPhysGarrison(root, at, level, race));
        if (type == "MINE")
            return _NEW(MachPhysMine(root, at, level, race));
        if (type == "BEACON")
            return _NEW(MachPhysBeacon(root, at, level, race));
    }
    return NULL;
}

// Loads a planet the way the game does (MachPhysPlanetSurface reads pdata/<planet>.psb from the game folder and
// the .env next to the .psf) and writes its terrain tiles as one model, plus a height grid sampled with the
// engine's own terrainHeight. Returns the number of triangles.
// The sky's meshes, as EnvSatellite::loadMesh and EnvSkyline load them: each satellite's mesh (named after the
// satellite in its .x file) and the skyline, written to sky/<name>.json.
void exportSky(W4dEntity* root, const std::string& psf, const std::string& out, std::set<std::string>* textures)
{
    std::ifstream env((psf.substr(0, psf.find_last_of('.')) + ".env").c_str());
    mkdir((out + "/sky").c_str());
    std::string name;
    for (std::string line; std::getline(env, line);)
    {
        const size_t quote = line.find('"');
        const std::string quoted = quote == std::string::npos ? "" : line.substr(quote + 1, line.find('"', quote + 1) - quote - 1);
        // A block starts at the left margin: a satellite or the skyline has a mesh to export, others do not.
        if (not line.empty() and not isspace((unsigned char)line[0]) and line[0] != '{' and line[0] != '}' and line[0] != '/')
            name = line.compare(0, 10, "satellite:") == 0 ? quoted
                : line.compare(0, 8, "skyline:") == 0 ? "skyline"
                // The sky dome (EnvDynamicSky, EnvStaticSky), its mesh called "sky".
                : line.compare(0, 12, "dynamic sky:") == 0 or line.compare(0, 11, "static sky:") == 0 ? "sky"
                : "";
        const size_t key = line.find("mesh file:");
        if (name.empty() or key == std::string::npos or quoted.empty())
            continue;

        // The mesh's textures sit in its planet's texture folder (models/planet/<planet>/sky/x.x).
        const std::string folder = quoted.substr(0, quoted.find("/sky/"));
        RenTexManager::PathNames searchList = RenTexManager::instance().searchList();
        searchList.push_back(SysPathName((folder + "/texture4").c_str()));
        RenTexManager::instance().searchList(searchList);

        W4dGeneric* sky = _NEW(W4dGeneric(root, MexTransform3d()));
        if (sky->loadSingleMesh(SysPathName(quoted.c_str()), name == "skyline" ? "skyline" : name))
        {
            ModelWriter writer;
            writer.addEntity(*sky);
            writer.write(out + "/sky/" + name + ".json", name);
            textures->insert(writer.textures().begin(), writer.textures().end());
            std::cout << "sky: " << name << " from " << quoted << ", " << writer.triangleCount() << " triangles" << std::endl;
        }
        name.clear();
    }
}

// The scenery and destructible objects the scenarios set on the planet (ARTEFACTS, MachLogArtefacts::load): the
// game keeps their models in pdata/<planet>.arb, a persisted W4dGenericRepository; each model is written to
// artefacts/<name>.json under its name in the repository, the name the scenarios use.
void exportArtefacts(const std::string& psf, const std::string& out, std::set<std::string>* textures)
{
    const size_t slash = psf.find_last_of("/\\");
    const std::string file = psf.substr(slash == std::string::npos ? 0 : slash + 1);
    const std::string planet = file.substr(0, file.find_last_of('.'));
    const SysPathName arb(("pdata/" + planet + ".arb").c_str());
    if (not arb.existsAsFile())
        return;

    // Their textures sit in the planet's texture folder.
    RenTexManager::PathNames searchList = RenTexManager::instance().searchList();
    searchList.push_back(SysPathName(("models/planet/" + planet + "/texture4").c_str()));
    RenTexManager::instance().searchList(searchList);

    W4dGenericRepository* repository = _NEW(W4dGenericRepository);
    std::ifstream in(arb.c_str(), std::ios::binary);
    PerIstream persisted(in);
    persisted >> (*repository);
    mkdir((out + "/artefacts").c_str());
    for (uint i = 0; i < repository->nGenerics(); ++i)
    {
        const std::string name(repository->key(i).c_str());
        // The repository keeps its models' levels of detail in no fixed order (a palm's first is its bare trunk);
        // the richest is written.
        const W4dEntity& model = repository->entity(repository->key(i));
        W4dLOD richest = 0;
        size_t most = 0;
        for (W4dLOD lod = 0; lod < 4; ++lod)
        {
            if (not model.hasMesh(lod))
                continue;
            Ren::ConstMeshPtr mesh = model.mesh(lod).mesh();
            size_t count = mesh->ttfs_.size() + mesh->stfps_.size();
            for (size_t g = 0; g < mesh->triangles_.size(); ++g)
                count += mesh->triangles_[g]->nTriangles();
            if (count > most)
                (most = count, richest = lod);
        }
        ModelWriter writer;
        writer.lod(richest);
        writer.addEntity(model);
        writer.write(out + "/artefacts/" + name + ".json", name);
        textures->insert(writer.textures().begin(), writer.textures().end());
        std::cout << "artefact: " << name << ", " << writer.triangleCount() << " triangles" << std::endl;
    }
}

size_t exportPlanet(W4dSceneManager* manager, W4dEntity* root, const std::string& psf, const std::string& out, std::set<std::string>* textures)
{
    exportSky(root, psf, out, textures);
    exportArtefacts(psf, out, textures);

    // The tiles are entities of the scene; the surface is left alive until the process ends.
    MachPhysPlanetSurface* surface = _NEW(MachPhysPlanetSurface(manager, SysPathName(psf.c_str())));

    ModelWriter writer;
    for (size_t y = 0; y < surface->nTilesY_; ++y)
        for (size_t x = 0; x < surface->nTilesX_; ++x)
            writer.addEntity(*surface->tileArray_[y][x]);
    mkdir(out.c_str());
    writer.write(out + "/terrain.json", "terrain");
    textures->insert(writer.textures().begin(), writer.textures().end());

    // heights.bin: int32 columns, int32 rows, float32 spacing in metres, then float32 heights row by row
    // (y outer, x inner), starting at (0, 0).
    const float spacing = 4;
    const MATHEX_SCALAR xMax = surface->xMax(), yMax = surface->yMax();
    const int32_t columns = int32_t(xMax / spacing) + 1, rows = int32_t(yMax / spacing) + 1;
    std::ofstream heights((out + "/heights.bin").c_str(), std::ios::binary);
    heights.write(reinterpret_cast<const char*>(&columns), 4);
    heights.write(reinterpret_cast<const char*>(&rows), 4);
    heights.write(reinterpret_cast<const char*>(&spacing), 4);
    for (int32_t y = 0; y < rows; ++y)
        for (int32_t x = 0; x < columns; ++x)
        {
            const float h = surface->terrainHeight(std::min<MATHEX_SCALAR>(x * spacing, xMax - 0.01),
                                                   std::min<MATHEX_SCALAR>(y * spacing, yMax - 0.01));
            heights.write(reinterpret_cast<const char*>(&h), 4);
        }
    return writer.triangleCount();
}

std::string modelId(const std::vector<std::string>& words)
{
    std::string id;
    for (size_t i = 1; i < words.size(); ++i)
        id += (i > 1 ? "_" : "") + words[i];
    return id;
}

} // namespace

ExportApp::ExportApp() : exitCode_(0) {}

ExportApp::~ExportApp() {}

bool ExportApp::clientStartup()
{
    // Planet mode: --planet <game folder> <planet .psf> <output folder> <snddef64.dat>
    const bool planet = invokeArgs().size() == 5 and std::string(invokeArgs()[0].c_str()) == "--planet";
    const size_t first = planet ? 1 : 0;
    if (invokeArgs().size() != 4 and not planet)
    {
        std::cerr << "usage: classic_export <game folder> <model list> <output folder> <snddef64.dat>\n"
                  << "       classic_export --planet <game folder> <planet .psf> <output folder> <snddef64.dat>" << std::endl;
        exitCode_ = 2;
        finish();
        return true;
    }

    const std::string game = invokeArgs()[first].c_str();
    const std::string input = invokeArgs()[first + 1].c_str();
    std::ifstream list(input.c_str());
    const std::string out = invokeArgs()[first + 2].c_str();
    const std::string soundDefinitions = invokeArgs()[first + 3].c_str();
    if (not list)
    {
        std::cerr << "cannot read " << invokeArgs()[1].c_str() << std::endl;
        exitCode_ = 2;
        finish();
        return true;
    }
    std::vector<std::vector<std::string> > models;
    for (std::string line; not planet and std::getline(list, line);)
    {
        std::istringstream words(line);
        std::vector<std::string> w;
        for (std::string word; words >> word;)
            w.push_back(word);
        if (w.size() >= 3)
            models.push_back(w);
    }

    if (chdir(game.c_str()) != 0)
    {
        std::cerr << "cannot open game folder " << game << std::endl;
        exitCode_ = 2;
        finish();
        return true;
    }

    // The same start-up as the original planet editor: render library, sound (machines attach their engine
    // sounds when built), display, scene manager, textures.
    Ren::initialise();
    SndMixerParameters soundParams(Snd::Polyphony(5), SndMixerParameters::DIRECTSOUND, Snd::ELEVEN_THOUSAND_HZ,
        Snd::SIXTEEN_BIT, Snd::STEREO, Snd::THREE_D, 20);
    SndMixer::initialise(soundParams);
    MachPhysPreload::registerSounds();
    W4dSoundManager::instance().readSoundDefinitionFile(SysPathName(soundDefinitions.c_str()));

    W4dRoot* root = _NEW(W4dRoot(W4dRoot::W4dRootId()));
    RenDisplay* display = _NEW(RenDisplay(window()));
    display->buildDisplayModesList();
    display->useMode(640, 480, 0);
    W4dSceneManager* manager = _NEW(W4dSceneManager(display, root));
    W4dManager::instance().sceneManager(manager);

    RenTexManager::PathNames searchList = RenTexManager::instance().searchList();
    searchList.push_back(SysPathName("models/texture4"));
    searchList.push_back(SysPathName("models/texture4/exp"));
    searchList.push_back(SysPathName("models/texture4/fire"));
    searchList.push_back(SysPathName("models/texture4/smoke"));
    if (planet)
    {
        // The planet's own textures: the surface would look next to the .psf, which is outside the game folder.
        std::string name = input.substr(input.find_last_of("/\\") + 1);
        name = name.substr(0, name.find('.'));
        searchList.push_back(SysPathName(("models/planet/" + name + "/texture4").c_str()));
    }
    RenTexManager::instance().searchList(searchList);

    if (planet)
    {
        std::set<std::string> textures;
        std::cout << "loading planet " << input << " ..." << std::endl;
        MachPhysPersistence::instance();
        const size_t triangles = exportPlanet(manager, root, input, out, &textures);
        std::ofstream textureList((out + "/textures.txt").c_str());
        for (std::set<std::string>::const_iterator it = textures.begin(); it != textures.end(); ++it)
            textureList << *it << "\n";
        std::cout << "terrain: " << triangles << " triangles, " << textures.size() << " textures in " << out << std::endl;
        finish();
        return true;
    }

    std::cout << "loading models.bin ..." << std::endl;
    MachPhysPersistence::instance();
    MachPhysPreload::persistentFileName("models.bin");
    MachPhysPreload::persistentPreload(MachPhysPreload::QUIET_PROGRESS, NULL);

    std::set<std::string> textures;
    const MachPhys::Race races[] = { MachPhys::RED, MachPhys::BLUE, MachPhys::GREEN, MachPhys::YELLOW };
    int written = 0;
    for (int r = 0; r < 4; ++r)
    {
        const std::string folder = out + "/" + raceName(races[r]);
        mkdir(out.c_str());
        mkdir(folder.c_str());
        for (size_t m = 0; m < models.size(); ++m)
        {
            W4dEntity* object = build(root, models[m], races[r]);
            if (object == NULL)
            {
                std::cerr << "skipped " << modelId(models[m]) << std::endl;
                continue;
            }
            ModelWriter writer;
            writer.addEntity(*object);
            {
                std::ofstream out((folder + "/" + modelId(models[m]) + ".json").c_str());
                out << std::setprecision(6) << "{\"id\":\"" << modelId(models[m]) << "\",";
                writer.writeParts(out);
                if (W4dComposite* composite = dynamic_cast<W4dComposite*>(object))
                {
                    out << ",\n";
                    writeRig(out, *composite, &textures);
                }
                out << "}\n";
            }
            textures.insert(writer.textures().begin(), writer.textures().end());
            if (r == 0)
                std::cout << modelId(models[m]) << ": " << writer.triangleCount() << " triangles" << std::endl;
            _DELETE(object);
            ++written;
        }
    }

    // The weapons' projectiles and impact splats, race-free, into fx/<name>.json. They live only in models.bin,
    // so each is made the way the game makes it (machphys: plasmbol, pulseblb, missile, stingl/m, plasplat,
    // plasaura, pulsplat, pulsaura).
    static const char* effects[] = {
        "plaboltr", "plaboltc", "plabolt2", "pulboltr", "pulboltc",
        "mis1", "mis2", "mis3", "mis4", "mis5", "mis6", "mis7", "lmiss", "nmisea",
        "stinglight", "stingmetal", "splat", "aura", "pulsesplat", "pulseaura", "torch", "sparks",
        "punchcrack", "punchfire", "beecrack", "beefire", "beewave",
        "helix1", "helix2", "healcross", "healaura", "disc", "lightning", "lightbox",
        "loadunder", "loadover", "loadlight", "fire",
        "ionbeam", "ionflare", "ionouter", "ionwave", "nukewave", "mushtop", "mushshaft", "vsphere", "bsphere", "wsphere",
        "lmine", "debris",
    };
    mkdir((out + "/fx").c_str());
    for (size_t e = 0; e < sizeof(effects) / sizeof(effects[0]); ++e)
    {
        const std::string name = effects[e];
        const MexTransform3d at;
        W4dEntity* effect = NULL;
        const W4dEntity* link = NULL;
        if (name == "plaboltr") effect = _NEW(MachPhysPlasmaBolt(root, at, MachPhys::PLASMA_RIFLE, MachPhysPlasmaBolt::DONT_CREATE_LIGHTS));
        else if (name == "plaboltc") effect = _NEW(MachPhysPlasmaBolt(root, at, MachPhys::PLASMA_CANNON1, MachPhysPlasmaBolt::DONT_CREATE_LIGHTS));
        else if (name == "plabolt2") effect = _NEW(MachPhysPlasmaBolt(root, at, MachPhys::PLASMA_CANNON2, MachPhysPlasmaBolt::DONT_CREATE_LIGHTS));
        else if (name == "pulboltr") effect = _NEW(MachPhysPulseBlob(root, at, MachPhys::PULSE_RIFLE, MachPhysPulseBlob::DONT_CREATE_LIGHTS));
        else if (name == "pulboltc") effect = _NEW(MachPhysPulseBlob(root, at, MachPhys::PULSE_CANNON, MachPhysPulseBlob::DONT_CREATE_LIGHTS));
        else if (name.compare(0, 3, "mis") == 0) effect = _NEW(MachPhysMissile(root, W4dTransform3d(), size_t(name[3] - '0'), MachPhysMissile::DONT_CREATE_LIGHTS));
        // MachPhysTrailedProjectile: MISSILE_LARGE 8, NUCLEAR_MISSILE 9.
        else if (name == "lmiss") effect = _NEW(MachPhysMissile(root, W4dTransform3d(), 8, MachPhysMissile::DONT_CREATE_LIGHTS));
        else if (name == "nmisea") effect = _NEW(MachPhysMissile(root, W4dTransform3d(), 9, MachPhysMissile::DONT_CREATE_LIGHTS));
        else if (name == "stinglight") effect = _NEW(MachPhysLightSting(root, at));
        else if (name == "stingmetal") effect = _NEW(MachPhysMetalSting(root, at));
        else if (name == "splat") effect = _NEW(MachPhysPlasmaSplat(root, at));
        else if (name == "aura") effect = _NEW(MachPhysPlasmaAura(root, at));
        else if (name == "pulsesplat") effect = _NEW(MachPhysPulseSplat(root, at));
        else if (name == "pulseaura") effect = _NEW(MachPhysPulseAura(root, at));
        // A constructor at work: the green beam from its jets (MachPhysJetRing, 5 m long) and their sparks.
        else if (name == "torch") effect = _NEW(MachPhysJetRing(root, at));
        else if (name == "sparks") effect = _NEW(MachPhysSparks(root, at));
        // The Gorilla's punch and the bee bomb tear the ground (MachPhysCrackFire, models/weapons/punch and
        // bee/chasm.cdf): one section 2 m long (unscaled), its crack and its wall of fire written apart, as the
        // game scales them apart. The bee bomb's shock wave spreads with them (MachPhysBeeBombWave).
        else if (name == "beewave") effect = _NEW(MachPhysBeeBombWave(root, at));
        // The support weapons' and the transporters' effects, made as their classes make them from models.bin
        // (the loose .lod files are not in the game copy): the healing helices (MachPhysHelix) and crosses
        // (MachPhysHealAura), the treachery orb's disc (MachPhysRadialDisc), the electric charger's lightning and
        // the halo round its victim (MachPhysElectro, MachPhysLightningHalo), ore loading's rings and light
        // (MachPhysResourceLoading) and the flame of burning (MachPhysDoublesidedFlame).
        else if (name == "helix1") effect = _NEW(MachPhysHelix(root, at, MachPhysHelix::LEFT_TWIST));
        else if (name == "helix2") effect = _NEW(MachPhysHelix(root, at, MachPhysHelix::RIGHT_TWIST));
        else if (name == "healcross")
            effect = _NEW(W4dGeneric(MachPhysOtherPersistence::instance().healAuraRadialCrossExemplar(), root, W4dTransform3d()));
        else if (name == "healaura")
            effect = _NEW(W4dGeneric(MachPhysOtherPersistence::instance().healAuraCentralCrossExemplar(), root, W4dTransform3d()));
        else if (name == "disc") effect = _NEW(MachPhysRadialDisc(root, at));
        else if (name == "lightning") effect = _NEW(MachPhysElectro(root, at, MachPhysElectro::ELECTRIC));
        else if (name == "lightbox") effect = _NEW(MachPhysLightningHalo(root, at, MachPhysLightningHalo::ELECTRIC));
        else if (name == "loadunder") effect = _NEW(MachPhysLoadingUnder(root, at));
        else if (name == "loadover") effect = _NEW(MachPhysLoadingOver(root, at));
        else if (name == "loadlight") effect = _NEW(MachPhysLoadingLight(root, at));
        else if (name == "fire") effect = _NEW(MachPhysDoublesidedFlame(root, at));
        // The area weapons: the ion cannon's beam, flare, outer shell and wave (the links of MachPhysBeam, each
        // written on its own, as each grows and shrinks on its own), the nuclear blast's shock wave and mushroom
        // cloud (MachPhysNukeWave, MachPhysMushroomTop and Shaft) and the vortex's three spheres.
        else if (name.compare(0, 3, "ion") == 0)
        {
            MachPhysBeam* beam = _NEW(MachPhysBeam(root, at));
            W4dLink* found = NULL;
            if (not beam->findLink(name.substr(3).c_str(), &found))
                continue;
            found->visible(true);
            effect = beam;
            link = found;
        }
        else if (name == "nukewave") effect = _NEW(MachPhysNukeWave(root, at));
        else if (name == "mushtop") effect = _NEW(MachPhysMushroomTop(root, at));
        else if (name == "mushshaft") effect = _NEW(MachPhysMushroomShaft(root, at));
        else if (name == "vsphere") effect = _NEW(MachPhysVortexSphere(root, at));
        else if (name == "bsphere") effect = _NEW(MachPhysBlackSphere(root, at));
        else if (name == "wsphere") effect = _NEW(MachPhysWhiteSphere(root, at));
        // A spy's land mine (MachPhysLandMine, models/weapons/lmine/lmine.lod in models.bin).
        else if (name == "lmine")
            effect = _NEW(MachPhysLandMine(root, W4dTransform3d(), MachPhys::RED));
        // The wreck a destroyed machine or building leaves (MachPhysDebrisUnit, the debris exemplar in models.bin).
        else if (name == "debris")
            effect = _NEW(MachPhysDebrisUnit(root, W4dTransform3d(), MexAlignedBox2d(-2, -2, 2, 2), 10.0));
        const W4dEntity* part = link != NULL ? link : effect;
        if (name == "punchcrack" or name == "punchfire" or name == "beecrack" or name == "beefire")
        {
            MachPhysCrackFire* chasm = _NEW(MachPhysCrackFire(root, MexPoint3d(0, 0, 0), MexPoint3d(2, 0, 0),
                name[0] == 'p' ? MachPhysCrackFire::GARILLA_PUNCH_CHASM : MachPhysCrackFire::BEE_BOMB_CHASM));
            W4dLink* link = NULL;
            if (not chasm->findLink(name.find("crack") != std::string::npos ? "Crack23" : "Fire23", &link))
                continue;
            link->visible(true);
            effect = chasm;
            part = link;
        }
        if (effect == NULL)
            continue;
        // The game makes splats, auras and the wave hidden and shows them when they go off.
        if (part == effect)
            effect->visible(true);
        ModelWriter writer;
        writer.addEntity(*part);
        writer.write(out + "/fx/" + name + ".json", name);
        textures.insert(writer.textures().begin(), writer.textures().end());
        std::cout << "fx " << name << ": " << writer.triangleCount() << " triangles" << std::endl;
    }

    std::ofstream textureList((out + "/textures.txt").c_str());
    for (std::set<std::string>::const_iterator it = textures.begin(); it != textures.end(); ++it)
        textureList << *it << "\n";

    std::cout << written << " models and " << textures.size() << " textures listed in " << out << std::endl;
    finish();
    return true;
}

void ExportApp::clientShutdown() {}

void ExportApp::loopCycle() { finish(); }

void ExportApp::updateDisplay() {}

bool ExportApp::activate() { return true; }

const string& ExportApp::name() const
{
    static const string name = "Machines classic exporter";
    return name;
}
