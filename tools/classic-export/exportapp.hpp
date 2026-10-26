/*
 * Classic model exporter for the Machines remake.
 *
 * Loads models.bin from a purchased copy of Machines: Wired for War with the original engine code
 * (the GPLv3 source release), builds every machine and building the way the game does, and writes
 * their visible geometry as JSON for the Godot client. Output is for local use with your own copy only.
 *
 * GPLv3, like the engine it links against.
 */
#ifndef CLASSIC_EXPORT_APP_HPP
#define CLASSIC_EXPORT_APP_HPP

#define MOST_DERIVED_APP ExportApp

#include <string>
#include "afx/osapp.hpp"

class ExportApp : public AfxOSSpecificApp<ExportApp>
{
public:
    virtual ~ExportApp();

private:
    friend class AfxSingletonApp;
    ExportApp();

    virtual bool clientStartup();
    virtual void clientShutdown();
    virtual void loopCycle();
    virtual void updateDisplay();
    virtual bool activate();
    virtual const string& name() const;

    int exitCode_;
};

#endif
