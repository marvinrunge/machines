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
#include "render/internal/fixptvec.hpp"
#include "render/internal/trigroup.hpp"
#include "world4d/root.hpp"
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

// One material's worth of triangles, in model space (original axes: z up).
struct Bucket
{
    std::string texture;
    float diffuse[4];
    std::vector<float> positions;
    std::vector<float> normals;
    std::vector<float> uvs;
    std::vector<unsigned> indices;
};

class ModelWriter
{
public:
    ModelWriter() {}

    // Rig mode: geometry relative to `node`, leaving out the links of `rigRoot` (they are nodes of their own).
    ModelWriter(const W4dEntity* node, const W4dComposite* rigRoot) : node_(node), rigRoot_(rigRoot) {}

    void addEntity(const W4dEntity& entity, int depth = 0)
    {
        if (rigRoot_ != NULL and depth > 0)
        {
            const W4dLink* link = dynamic_cast<const W4dLink*>(&entity);
            if (link != NULL and link->pComposite() == rigRoot_)
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

        if (entity.visible() and entity.hasMesh())
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
                << b.diffuse[0] << "," << b.diffuse[1] << "," << b.diffuse[2] << "," << b.diffuse[3] << "]";
            writeArray(out, "positions", b.positions);
            writeArray(out, "normals", b.normals);
            writeArray(out, "uvs", b.uvs);
            out << ",\"indices\":[";
            for (size_t i = 0; i < b.indices.size(); ++i)
                out << (i ? "," : "") << b.indices[i];
            out << "]}";
            firstBucket = false;
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
        const RenMeshInstance& instance = entity.mesh();
        Ren::ConstMeshPtr mesh = instance.mesh();
        const RenIVertexData* vertices = mesh->vertices_;   // private: -fno-access-control
        if (vertices == NULL)
            return;

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
                    bucket.indices.push_back(bucket.indices.size());
                }
                ++triangles_;
            }
        }
    }

    Bucket& bucketFor(const RenMaterial& material)
    {
        std::string texture;
        if (not material.texture().isNull())
            texture = material.texture().name();
        const RenColour& d = material.diffuse();

        std::ostringstream key;
        key << texture << "|" << d.r() << "," << d.g() << "," << d.b() << "," << d.a();
        Bucket& bucket = buckets_[key.str()];
        if (bucket.texture.empty() and bucket.indices.empty())
        {
            bucket.texture = texture;
            bucket.diffuse[0] = d.r();
            bucket.diffuse[1] = d.g();
            bucket.diffuse[2] = d.b();
            bucket.diffuse[3] = d.a();
        }
        if (not texture.empty())
            textures_.insert(texture);
        return bucket;
    }

    std::map<std::string, Bucket> buckets_;
    std::set<std::string> textures_;
    size_t triangles_ = 0;
    const W4dEntity* node_ = NULL;
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
void writeRig(std::ostream& out, MachPhysMachine& machine, std::set<std::string>* textures)
{
    const W4dComposite::W4dLinks& links = machine.links();
    std::vector<const W4dEntity*> nodes(1, &machine);
    nodes.insert(nodes.end(), links.begin(), links.end());
    const auto indexOf = [&](const W4dEntity* entity) {
        for (size_t i = 0; i < nodes.size(); ++i)
            if (nodes[i] == entity)
                return int(i);
        return -1;
    };

    out << "\"rig\":{\"nodes\":[";
    for (size_t i = 0; i < nodes.size(); ++i)
    {
        const int parent = i == 0 ? -1 : std::max(0, indexOf(nodes[i]->pParent()));
        ModelWriter writer(nodes[i], &machine);
        writer.addEntity(*nodes[i]);
        textures->insert(writer.textures().begin(), writer.textures().end());
        out << (i ? "," : "") << "\n{\"parent\":" << parent << ",\"transform\":";
        writeTransform(out, i == 0 ? MexTransform3d() : nodes[i]->localTransform());
        out << ",";
        writer.writeParts(out);
        out << "}";
    }
    out << "],\"upperBody\":" << indexOf(machine.pImpl_->pUpperBodyTurnLink_);

    MachPhysLocomotionMethod& locomotion = machine.locomotionMethod();
    if (MachPhysSpiderLegs* legs = dynamic_cast<MachPhysSpiderLegs*>(&locomotion))
    {
        const MachPhysSpiderLegsImpl& impl = *legs->pImpl_;
        out << ",\"animations\":{";
        bool first = true;
        writeAnimation(out, "rest", impl.restingPlanPtr_, links, &first);
        writeAnimation(out, "walk", impl.walkingPlanPtr_, links, &first);
        writeAnimation(out, "turnLeft", impl.turningLeftPlanPtr_, links, &first);
        writeAnimation(out, "turnRight", impl.turningRightPlanPtr_, links, &first);
        writeAnimation(out, "startWalking", impl.startWalkingPlanPtr_, links, &first);
        writeAnimation(out, "stopWalking", impl.stopWalkingPlanPtr_, links, &first);
        out << "}";
    }
    else if (MachPhysWheels* wheels = dynamic_cast<MachPhysWheels*>(&locomotion))
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
size_t exportPlanet(W4dSceneManager* manager, const std::string& psf, const std::string& out, std::set<std::string>* textures)
{
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
        const size_t triangles = exportPlanet(manager, input, out, &textures);
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
                if (MachPhysMachine* machine = dynamic_cast<MachPhysMachine*>(object))
                {
                    out << ",\n";
                    writeRig(out, *machine, &textures);
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
