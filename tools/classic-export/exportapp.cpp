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
    std::vector<float> uvs;
    std::vector<unsigned> indices;
};

class ModelWriter
{
public:
    void addEntity(const W4dEntity& entity, int depth = 0)
    {
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

        // A composite's parts (links) are not ordinary children: they form their own subtree.
        if (const W4dComposite* composite = dynamic_cast<const W4dComposite*>(&entity))
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

    void write(const std::string& path, const std::string& id) const
    {
        std::ofstream out(path.c_str());
        out << std::setprecision(6);
        out << "{\"id\":\"" << id << "\",\"parts\":[";
        bool firstBucket = true;
        for (std::map<std::string, Bucket>::const_iterator it = buckets_.begin(); it != buckets_.end(); ++it)
        {
            const Bucket& b = it->second;
            if (b.indices.empty())
                continue;
            out << (firstBucket ? "" : ",") << "\n{\"texture\":\"" << b.texture << "\",\"diffuse\":["
                << b.diffuse[0] << "," << b.diffuse[1] << "," << b.diffuse[2] << "," << b.diffuse[3] << "]";
            writeArray(out, "positions", b.positions);
            writeArray(out, "uvs", b.uvs);
            out << ",\"indices\":[";
            for (size_t i = 0; i < b.indices.size(); ++i)
                out << (i ? "," : "") << b.indices[i];
            out << "]}";
            firstBucket = false;
        }
        out << "\n]}\n";
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

        const MexTransform3d transform = modelTransform(entity);
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
};

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
    if (invokeArgs().size() != 4)
    {
        std::cerr << "usage: classic_export <game folder> <model list> <output folder> <snddef64.dat>" << std::endl;
        exitCode_ = 2;
        finish();
        return true;
    }

    const std::string game = invokeArgs()[0].c_str();
    std::ifstream list(invokeArgs()[1].c_str());
    const std::string out = invokeArgs()[2].c_str();
    if (not list)
    {
        std::cerr << "cannot read " << invokeArgs()[1].c_str() << std::endl;
        exitCode_ = 2;
        finish();
        return true;
    }
    std::vector<std::vector<std::string> > models;
    for (std::string line; std::getline(list, line);)
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
    W4dSoundManager::instance().readSoundDefinitionFile(SysPathName(invokeArgs()[3].c_str()));

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
    RenTexManager::instance().searchList(searchList);

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
            writer.write(folder + "/" + modelId(models[m]) + ".json", modelId(models[m]));
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
