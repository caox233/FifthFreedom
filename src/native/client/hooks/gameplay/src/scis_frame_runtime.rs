//! Value-only scene coordination. GPU inspection runs outside the shared lock;
//! the final saved-camera commit is render-thread-owned and rechecks the join.
use crate::scis_diagnostics as diag;
use crate::{
    scis_frame_commit::{self, CameraMemory},
    scis_frame_preparation::{self, Frame},
    scis_frame_projection::Matrix,
    scis_reticle_content::Reticle,
    scis_scene_source,
    scis_scene_transport::Transport,
};
use std::sync::Mutex;

#[derive(Default)]
struct State {
    epoch: u64,
    transport: Transport,
    batches: [(u32, u64); 4],
}
impl State {
    fn sync(&mut self) {
        let epoch = scis_scene_source::binding_epoch();
        if self.epoch != epoch {
            self.transport.reset();
            self.batches = [(0, 0); 4];
            self.epoch = epoch;
        }
    }
}
#[derive(Default)]
pub(crate) struct Runtime(Mutex<State>);
impl Runtime {
    pub(crate) fn publish(&self, command: u32, provider: u32, info: u32) -> bool {
        let Ok(mut s) = self.0.lock() else {
            return false;
        };
        s.sync();
        s.transport.publish(command, provider, info)
    }
    pub(crate) fn admitted(
        &self,
        command: u32,
        provider: u32,
        before: u32,
        after: u32,
        index: u32,
    ) -> bool {
        let Ok(mut s) = self.0.lock() else {
            return false;
        };
        s.sync();
        s.transport
            .admitted(command, provider, before, after, index)
    }
    pub(crate) fn drained(&self, provider: u32) -> Option<u64> {
        let mut s = self.0.lock().ok()?;
        s.sync();
        let Some(slot) = s
            .batches
            .iter()
            .position(|v| v.0 == provider)
            .or_else(|| s.batches.iter().position(|v| v.0 == 0))
        else {
            s.transport.reset();
            s.batches = [(0, 0); 4];
            return None;
        };
        let batch = s.transport.drained(provider);
        s.batches[slot] = batch.map_or((0, 0), |b| (provider, b));
        batch
    }
    pub(crate) fn finished(&self, provider: u32) {
        if let Ok(mut s) = self.0.lock() {
            s.sync();
            s.transport.finished(provider);
            for v in &mut s.batches {
                if v.0 == provider {
                    *v = (0, 0);
                }
            }
        }
    }
    pub(crate) fn reset(&self) {
        if let Ok(mut s) = self.0.lock() {
            s.transport.reset();
            s.batches = [(0, 0); 4];
            s.epoch = scis_scene_source::binding_epoch();
        }
    }
    /// Entered at native16DF0D8, before the ordinary +70 call. The renderer
    /// passed by the caller is OUTER; the camera consumer lives at outer+2C.
    /// No pointers from an earlier frame or the options pointer at outer+40
    /// can substitute for that embedded renderer.
    pub(crate) unsafe fn render(
        &self,
        memory: &impl CameraMemory,
        provider: u32,
        info: u32,
        outer: u32,
        identify: impl FnOnce(u32, crate::scis_scene_source::Scope) -> Option<Reticle>,
        multiply: impl Fn(&Matrix, &Matrix) -> Matrix,
    ) -> bool {
        diag::hit(diag::RENDER_ENTER);
        let Some(renderer) = outer.checked_add(0x2c) else {
            return false;
        };
        let Some(settings) = info.checked_add(0xe0) else {
            return false;
        };
        if memory.pointer(provider, 0x4378) != Some(outer)
            || memory.pointer(renderer, 0x20d0) != Some(settings)
        {
            diag::hit(diag::RENDER_POINTER_REJECT);return false;
        }
        let (Some(count), Some(index), Some(flags)) = (
            memory.at(provider, 0x4330),
            memory.at(info, 8),
            memory.at(settings, 0),
        ) else {
            return false;
        };
        let Some((frame, entry)) = (|| {
            let mut s = self.0.lock().ok()?;
            s.sync();
            let batch = (s.batches.iter().find(|v| v.0 == provider)).or_else(||{diag::hit(diag::RENDER_BATCH_MISSING);None})?.1;
            let frame = Frame {
                provider,
                batch,
                info,
                renderer,
            };
            let entry = s
                .transport
                .selected(provider, batch, info, count, index, flags).or_else(||{diag::hit(diag::RENDER_SELECTION_REJECT);None})?;
            Some((frame, entry))
        })() else {
            return false;
        };
        diag::hit(diag::RENDER_JOINED);
        #[cfg(feature = "scis-runtime-diagnostics")]
        crate::scis_frame_native::observe_source(renderer, entry.source);
        // Inspection may call COM/native methods. It must not run under State.
        let Some(p) = scis_frame_preparation::prepare_entry(
            entry,
            frame,
            memory,
            |_node| identify(renderer, entry.source.scope),
            multiply,
        ) else {
            return false;
        };
        let Ok(mut s) = self.0.lock() else {
            return false;
        };
        s.sync();
        if memory.pointer(provider, 0x4378) != Some(outer)
            || !s.batches.contains(&(provider, frame.batch))
        {
            return false;
        }
        let committed=scis_frame_commit::commit(&mut s.transport, memory, &p);
        #[cfg(feature="scis-runtime-diagnostics")] crate::scis_draw_diagnostics::committed(memory,p.frame.renderer,p.nonce,committed);
        diag::trace(diag::PROJECTION_RESULT,||[
            p.frame.batch as u32,(p.frame.batch>>32)as u32,p.frame.renderer,p.source.scope.node,
            p.settings,p.record,u32::from(committed),p.source.aim.request.scene,
            p.correction.reticle_ndc[0].to_bits(),p.correction.reticle_ndc[1].to_bits(),
            p.correction.original_aim_ndc[0].to_bits(),p.correction.original_aim_ndc[1].to_bits(),
            p.correction.primary[8].to_bits(),p.correction.primary[9].to_bits(),
            memory.at(p.settings,0xf0).unwrap_or(0),memory.at(p.settings,0xf4).unwrap_or(0)]);
        if committed{diag::hit(diag::RENDER_COMMITTED);}else{diag::hit(diag::RENDER_COMMIT_REJECT);}
        committed
    }
}
