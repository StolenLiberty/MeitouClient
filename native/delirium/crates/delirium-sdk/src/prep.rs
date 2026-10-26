// Meitou: reads a GTA IV PC install and writes what Delirium needs to run a character, for use outside the playroom:
//   fred.art, wilma.art (the euphoria character files, copied) and build-male.txt, build-female.txt (each body: parts, masses, joint frames, standing pose).
// The reading follows delirium-room's getup.rs (key, img_open, img_read, homer_build, Clips::load/pose/place/stand_up), cut down to what a build needs.
use delirium::prelude::*;
use delirium::ragdoll::Build;
use delirium_data::{anim, archive_key::ArchiveKey, img, model, resource, rpf, skeleton};
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

fn key(dir: &Path) -> Result<ArchiveKey, String> {
    delirium_data::LE.store(true, std::sync::atomic::Ordering::Relaxed);
    let exe = std::fs::read(dir.join("GTAIV.exe")).map_err(|e| format!("GTAIV.exe: {e}"))?;
    let mut head = vec![0; 0x810];
    std::fs::File::open(dir.join("pc/models/cdimages/playerped.rpf")).and_then(|mut f| f.read_exact(&mut head)).map_err(|e| format!("playerped.rpf: {e}"))?;
    let (_, k) = ArchiveKey::find_by_trial(&exe, &head).ok_or("no archive key in GTAIV.exe")?;
    Ok(ArchiveKey::from_bytes(&k))
}

fn img_open(path: &Path, key: &ArchiveKey) -> Result<(Vec<img::Entry>, std::fs::File), String> {
    let mut f = std::fs::File::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let mut head = vec![];
    (&mut f).take(4 << 20).read_to_end(&mut head).map_err(|e| e.to_string())?;
    Ok((img::read_table(&head, Some(key))?, f))
}

fn img_read(table: &[img::Entry], f: &mut std::fs::File, name: &str) -> Result<resource::Resource, String> {
    let e = table.iter().find(|e| e.name.eq_ignore_ascii_case(name)).ok_or(format!("{name} not found"))?;
    let mut raw = vec![];
    f.seek(SeekFrom::Start(e.offset)).map_err(|e| e.to_string())?;
    f.take(e.used_blocks as u64 * 2048).read_to_end(&mut raw).map_err(|e| e.to_string())?;
    resource::parse(&raw)
}

fn homer_build(dir: &Path, key: &ArchiveKey, model: &str, nm: usize) -> Result<Build, String> {
    let (table, mut f) = img_open(&dir.join("pc/models/cdimages/componentpeds.img"), key)?;
    let frag = img_read(&table, &mut f, &format!("{model}.wft"))?;
    let bones = skeleton::read(&frag, model::fragment_root(&frag)?)?;
    let parts = delirium_data::ragdoll::read(&frag)?;
    let parents: Vec<Option<usize>> = bones.iter().map(|b| b.parent).collect();
    let bind: Vec<(Vec3, Quat)> = bones.iter().map(|b| (Vec3::from(b.translation), Quat::from_array(b.rotation).normalize())).collect();
    delirium::ragdoll::Rig::new(&parts, &parents, &bind, &bones, nm).map(|r| r.build).ok_or(format!("{model}.wft: no ragdoll"))
}

/// The player's skeleton and the idle clip (move_player/idle), mapped to the build's parts: enough for the standing pose.
struct Idle {
    parents: Vec<Option<usize>>,
    bind: Vec<(Vec3, Quat)>,
    clip: anim::Animation,
    map: Vec<Option<usize>>,
    parts: Vec<(usize, Transform)>,
}

impl Idle {
    fn load(dir: &Path, key: &ArchiveKey, build: &Build) -> Result<Idle, String> {
        let peds = std::fs::read(dir.join("pc/models/cdimages/playerped.rpf")).map_err(|e| format!("playerped.rpf: {e}"))?;
        let inner = rpf::read_toc(&peds, key)?;
        let e = inner.iter().find(|e| e.path.eq_ignore_ascii_case("player.wft")).ok_or("player.wft not in playerped.rpf")?;
        let frag = resource::parse(&rpf::read(&peds, e)?)?;
        let bones = skeleton::read(&frag, model::fragment_root(&frag)?)?;
        let (table, mut f) = img_open(&dir.join("pc/anim/anim.img"), key)?;
        let clip = anim::read_dictionary(&img_read(&table, &mut f, "move_player.wad")?)?.into_iter().find(|a| a.name.eq_ignore_ascii_case("idle"))
            .ok_or("no idle in move_player.wad")?;
        let map = clip.tracks.iter().map(|t| bones.iter().position(|b| b.id == t.bone).filter(|&b| b != 0)).collect();
        let parts = build.parts.iter().map(|p| bones.iter().position(|b| b.name == p.name).map(|b| (b, p.offset)).ok_or(format!("no bone {}", p.name)))
            .collect::<Result<_, _>>()?;
        Ok(Idle {
            parents: bones.iter().map(|b| b.parent).collect(),
            bind: bones.iter().map(|b| (Vec3::from(b.translation), Quat::from_array(b.rotation).normalize())).collect(),
            clip, map, parts,
        })
    }

    fn pose(&self) -> Vec<(Vec3, Quat)> {
        let q = |v: &[f32]| Quat::from_xyzw(v[0], v[1], v[2], v[3]).normalize();
        let mut out = self.bind.clone();
        out[0] = (Vec3::ZERO, Quat::IDENTITY);
        for (t, bone) in self.clip.tracks.iter().zip(&self.map) {
            let v = t.sample(0);
            match (t.kind, bone, t.bone) {
                (anim::BONE_ROTATION, Some(b), _) => out[*b].1 = q(&v),
                (anim::BONE_TRANSLATION, Some(b), _) => out[*b].0 = Vec3::from_slice(&v),
                (anim::BONE_ROTATION, None, 0) => out[0].1 = q(&v),
                (anim::BONE_TRANSLATION | anim::MOVER_TRANSLATION, None, 0) => out[0].0 += Vec3::from_slice(&v),
                _ => {}
            }
        }
        out
    }

    fn place(&self, pose: &[(Vec3, Quat)], body: Transform) -> Vec<Transform> {
        let mut bones: Vec<(Vec3, Quat)> = Vec::with_capacity(pose.len());
        for (i, &(t, r)) in pose.iter().enumerate() {
            let (pt, pr) = self.parents[i].map_or((body.translation, body.rotation), |p| bones[p]);
            bones.push((pt + pr * t, pr * r));
        }
        self.parts.iter().map(|&(b, o)| {
            let (bt, br) = bones[b];
            Transform::from_translation(bt + br * o.translation).with_rotation(br * o.rotation)
        }).collect()
    }

    fn stand_up(&self, build: &mut Build) -> Vec<Transform> {
        let mut frames = self.place(&self.pose(), Transform::from_rotation(Quat::from_rotation_z(-std::f32::consts::FRAC_PI_2)));
        for (p, f) in build.parts.iter_mut().zip(&frames) {
            p.bind = f.rotation;
        }
        delirium::nmrs::asset_layout(build, &mut frames);
        frames
    }
}

/// Writes fred.art, wilma.art, build-male.txt and build-female.txt from the GTA IV PC install `dir` into `out`; the bodies' part counts and masses.
pub fn prepare(dir: &Path, out: &Path) -> Result<Vec<(String, usize, f32)>, String> {
    std::fs::create_dir_all(out).map_err(|e| e.to_string())?;
    let key = key(dir)?;
    let mut made = vec![];
    for (nm, art, model, file) in [(delirium::nmrs::HOMER, "Fred.art", "m_y_pmanhat_01", "build-male.txt"), (delirium::nmrs::MARGE, "Wilma.art", "f_y_pmanhat_01", "build-female.txt")] {
        let xrt = std::fs::read(dir.join("pc/data/naturalmotion").join(art)).map_err(|e| format!("{art}: {e}"))?;
        std::fs::write(out.join(art.to_lowercase()), &xrt).map_err(|e| e.to_string())?;
        delirium::nmrs::load(nm, &xrt);
        let mut b = homer_build(dir, &key, model, nm)?;
        let idle = Idle::load(dir, &key, &b)?;
        if !(nm == delirium::nmrs::HOMER && delirium::nmdbm::logged_links().is_some()) && delirium::nmdbm::DERIVED_LINKS[nm].get().is_none() {
            let stand: Vec<Quat> = idle.place(&idle.pose(), Transform::from_rotation(Quat::from_rotation_z(-std::f32::consts::FRAC_PI_2))).iter().map(|f| f.rotation).collect();
            if let Some(l) = delirium::nmrs::derive_links(&b, &stand) {
                let _ = delirium::nmdbm::DERIVED_LINKS[nm].set(l);
                b = homer_build(dir, &key, model, nm)?;
            }
        }
        let frames = idle.stand_up(&mut b);
        std::fs::write(out.join(file), b.dump(&frames)).map_err(|e| e.to_string())?;
        made.push((file.to_string(), b.parts.len(), b.parts.iter().map(|p| p.mass).sum::<f32>()));
    }
    Ok(made)
}
